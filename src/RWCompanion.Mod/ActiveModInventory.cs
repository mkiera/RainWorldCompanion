using System.Security.Cryptography;
using System.Text;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal sealed class ActiveModInventory : IDisposable
{
    private const int MaximumPathLength = 4096;
    private const int MaximumDirectoriesPerMod = 1024;
    private const int MaximumDllFilesPerMod = 256;
    private const long MaximumDllBytesPerMod = 128L * 1024 * 1024;
    private const long MaximumDllBytesPerInventory = 512L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _job;
    private string _signature = "";
    private LiveModInfo[] _mods = Array.Empty<LiveModInfo>();
    private bool _truncated;
    private long _generation;

    internal void Observe(IEnumerable<object> activeMods)
    {
        var all = activeMods.Take(ProtocolInfo.MaximumActiveMods + 1).Select(ReadSource)
            .OrderBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Id, StringComparer.Ordinal)
            .ThenBy(source => source.Name, StringComparer.Ordinal)
            .ThenBy(source => source.Version, StringComparer.Ordinal)
            .ToArray();
        bool truncated = all.Length > ProtocolInfo.MaximumActiveMods;
        var sources = all.Take(ProtocolInfo.MaximumActiveMods).ToArray();
        string signature = Signature(sources, truncated);
        CancellationTokenSource job;
        long generation;
        lock (_sync)
        {
            if (string.Equals(_signature, signature, StringComparison.Ordinal)) return;
            _signature = signature;
            _truncated = truncated;
            _mods = sources.Select(source => source.Info(source.CanInspect ? "pending" : "unavailable", "")).ToArray();
            generation = ++_generation;
            _job?.Cancel();
            job = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _job = job;
        }
        _ = Task.Run(() => Compute(sources, generation, job));
    }

    internal ActiveModInventorySnapshot Capture()
    {
        lock (_sync)
            return new(_mods.Select(Clone).ToArray(), _truncated);
    }

    private void Compute(ModSource[] sources, long generation, CancellationTokenSource job)
    {
        try
        {
            long remaining = MaximumDllBytesPerInventory;
            var result = new LiveModInfo[sources.Length];
            for (int index = 0; index < sources.Length; index++)
            {
                job.Token.ThrowIfCancellationRequested();
                result[index] = Fingerprint(sources[index], ref remaining, job.Token);
            }
            lock (_sync)
            {
                if (generation == _generation && !job.IsCancellationRequested) _mods = result;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_job, job)) _job = null;
            }
            job.Dispose();
        }
    }

    private static LiveModInfo Fingerprint(ModSource source, ref long inventoryRemaining, CancellationToken cancellationToken)
    {
        if (!source.CanInspect || !TryNormalizePath(source.ModRoot, out string modRoot)
            || ProbeDirectory(modRoot) != DirectoryProbe.Available)
            return source.Info("unavailable", "");
        string? codeRoot = SelectCodeRoot(source, modRoot, out bool partial);
        if (codeRoot is null) return source.Info(partial ? "unavailable" : "no-code", "");

        var files = DiscoverDlls(modRoot, codeRoot, ref partial, cancellationToken);
        if (files.Count == 0) return source.Info(partial ? "unavailable" : "no-code", "");

        long modRemaining = MaximumDllBytesPerMod;
        var digests = new List<FileDigest>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryHashFile(file, modRemaining, inventoryRemaining, out var digest))
            {
                partial = true;
                continue;
            }
            digests.Add(digest);
            modRemaining -= digest.Length;
            inventoryRemaining -= digest.Length;
        }
        if (digests.Count == 0) return source.Info("unavailable", "");

        using var framed = new MemoryStream();
        using (var writer = new BinaryWriter(framed, new UTF8Encoding(false), true))
        {
            writer.Write(new byte[] { 82, 87, 67, 77, 79, 68, 1 });
            writer.Write(digests.Count);
            foreach (var digest in digests.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
            {
                byte[] name = Encoding.UTF8.GetBytes(digest.RelativePath);
                writer.Write(name.Length);
                writer.Write(name);
                writer.Write(digest.Length);
                writer.Write(digest.Hash.Length);
                writer.Write(digest.Hash);
            }
        }
        using var combined = SHA256.Create();
        string fingerprint = Hex(combined.ComputeHash(framed.ToArray()));
        return source.Info(partial || digests.Count != files.Count ? "partial" : "complete", fingerprint);
    }

    private static string? SelectCodeRoot(ModSource source, string modRoot, out bool partial)
    {
        partial = false;
        foreach (string candidate in new[] { source.TargetedRoot, source.NewestRoot, source.ModRoot })
        {
            if (!TryNormalizePath(candidate, out string full) || !Contained(modRoot, full)) continue;
            DirectoryProbe rootState = ProbeDirectory(full);
            if (rootState == DirectoryProbe.Unavailable)
            {
                partial = true;
                continue;
            }
            if (rootState == DirectoryProbe.Missing) continue;
            DirectoryProbe plugins = ProbeDirectory(Path.Combine(full, "plugins"));
            DirectoryProbe patchers = ProbeDirectory(Path.Combine(full, "patchers"));
            if (plugins == DirectoryProbe.Unavailable || patchers == DirectoryProbe.Unavailable) partial = true;
            if (plugins == DirectoryProbe.Available || patchers == DirectoryProbe.Available) return full;
        }
        return null;
    }

    private static List<CodeFile> DiscoverDlls(string modRoot, string codeRoot, ref bool partial,
        CancellationToken cancellationToken)
    {
        var result = new List<CodeFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int directories = 0;
        foreach (string kind in new[] { "patchers", "plugins" })
        {
            string start = Path.Combine(codeRoot, kind);
            DirectoryProbe startState = ProbeDirectory(start);
            if (startState == DirectoryProbe.Missing) continue;
            if (startState == DirectoryProbe.Unavailable)
            {
                partial = true;
                continue;
            }
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                if (++directories > MaximumDirectoriesPerMod)
                {
                    partial = true;
                    return Sort(result);
                }
                try
                {
                    if (!Contained(modRoot, directory) || IsReparsePoint(directory))
                    {
                        partial = true;
                        continue;
                    }
                    int fileSlots = MaximumDllFilesPerMod - result.Count;
                    string[] fileCandidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                        .Take(fileSlots + 1).ToArray();
                    if (fileCandidates.Length > fileSlots) partial = true;
                    foreach (string path in fileCandidates.Take(fileSlots)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal))
                    {
                        string full = Path.GetFullPath(path);
                        if (!Contained(modRoot, full) || IsReparsePoint(full))
                        {
                            partial = true;
                            continue;
                        }
                        string relative = full.Substring(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')
                            .ToLowerInvariant();
                        if (relative.Length == 0 || !seen.Add(relative))
                        {
                            partial = true;
                            continue;
                        }
                        result.Add(new(full, relative));
                    }
                    if (partial && result.Count >= MaximumDllFilesPerMod) return Sort(result);
                    int directorySlots = Math.Max(0, MaximumDirectoriesPerMod - directories - pending.Count);
                    string[] childCandidates = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                        .Take(directorySlots + 1).ToArray();
                    if (childCandidates.Length > directorySlots) partial = true;
                    string[] children = childCandidates.Take(directorySlots)
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(path => path, StringComparer.Ordinal)
                        .ToArray();
                    foreach (string child in children) pending.Push(child);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                    or NotSupportedException or System.Security.SecurityException)
                {
                    partial = true;
                }
            }
        }
        return Sort(result);
    }

    private static List<CodeFile> Sort(List<CodeFile> files) => files
        .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
        .ToList();

    private static bool TryHashFile(CodeFile file, long modRemaining, long inventoryRemaining, out FileDigest digest)
    {
        digest = default;
        try
        {
            using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.SequentialScan);
            long length = stream.Length;
            if (length < 0 || length > modRemaining || length > inventoryRemaining) return false;
            DateTime written = File.GetLastWriteTimeUtc(file.FullPath);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            if (stream.Length != length || File.GetLastWriteTimeUtc(file.FullPath) != written) return false;
            digest = new(file.RelativePath, length, hash);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static ModSource ReadSource(object mod)
    {
        try
        {
            string id = Bound(GameAccess.Text(mod, "id"), ProtocolInfo.MaximumModIdLength);
            string name = Bound(GameAccess.Text(mod, "name"), ProtocolInfo.MaximumModDisplayNameLength);
            string version = Bound(GameAccess.Text(mod, "version"), ProtocolInfo.MaximumModVersionLength);
            if (id.Length == 0) id = "unknown";
            if (name.Length == 0) name = id;
            string root = InternalPath(GameAccess.Text(mod, "path"));
            string targeted = InternalPath(GameAccess.Text(mod, "TargetedPath"));
            string newest = InternalPath(GameAccess.Text(mod, "NewestPath"));
            return new(id, name, version, root, targeted, newest, root.Length > 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException or System.Reflection.TargetInvocationException)
        {
            return new("unknown", "Unknown mod", "", "", "", "", false);
        }
    }

    private static string Signature(IEnumerable<ModSource> sources, bool truncated)
    {
        var text = new StringBuilder(truncated ? "1" : "0");
        foreach (var source in sources)
        {
            Append(text, source.Id);
            Append(text, source.Name);
            Append(text, source.Version);
            Append(text, source.ModRoot);
            Append(text, source.TargetedRoot);
            Append(text, source.NewestRoot);
        }
        return text.ToString();
    }

    private static void Append(StringBuilder target, string value) => target.Append('|').Append(value.Length).Append(':').Append(value);

    private static string Bound(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string trimmed = value.Trim();
        var result = new StringBuilder(Math.Min(trimmed.Length, maximum));
        for (int index = 0; index < trimmed.Length && result.Length < maximum; index++)
        {
            char character = trimmed[index];
            if (char.IsControl(character) || char.IsLowSurrogate(character)) continue;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= trimmed.Length || !char.IsLowSurrogate(trimmed[index + 1])
                    || result.Length + 2 > maximum)
                    continue;
                result.Append(character).Append(trimmed[++index]);
                continue;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static string InternalPath(string value)
        => string.IsNullOrWhiteSpace(value) || value.Length > MaximumPathLength ? "" : value;

    private static bool TryNormalizePath(string value, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPathLength) return false;
        try
        {
            path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.Length > 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static DirectoryProbe ProbeDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0) return DirectoryProbe.Missing;
            return (attributes & FileAttributes.ReparsePoint) == 0
                ? DirectoryProbe.Available
                : DirectoryProbe.Unavailable;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return DirectoryProbe.Missing;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return DirectoryProbe.Unavailable;
        }
    }

    private static bool Contained(string root, string path)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException) { return true; }
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    private static LiveModInfo Clone(LiveModInfo mod) => new()
    {
        Id = mod.Id,
        DisplayName = mod.DisplayName,
        Version = mod.Version,
        CodeFingerprint = mod.CodeFingerprint,
        FingerprintStatus = mod.FingerprintStatus
    };

    public void Dispose()
    {
        lock (_sync)
        {
            _lifetime.Cancel();
            _job?.Cancel();
            _generation++;
        }
        _lifetime.Dispose();
    }

    private sealed class ModSource
    {
        internal ModSource(string id, string name, string version, string modRoot, string targetedRoot,
            string newestRoot, bool canInspect)
        {
            Id = id;
            Name = name;
            Version = version;
            ModRoot = modRoot;
            TargetedRoot = targetedRoot;
            NewestRoot = newestRoot;
            CanInspect = canInspect;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal string Version { get; }
        internal string ModRoot { get; }
        internal string TargetedRoot { get; }
        internal string NewestRoot { get; }
        internal bool CanInspect { get; }
        internal LiveModInfo Info(string status, string fingerprint) => new()
        {
            Id = Id,
            DisplayName = Name,
            Version = Version,
            CodeFingerprint = fingerprint,
            FingerprintStatus = status
        };
    }

    private readonly struct CodeFile
    {
        internal CodeFile(string fullPath, string relativePath)
        {
            FullPath = fullPath;
            RelativePath = relativePath;
        }

        internal string FullPath { get; }
        internal string RelativePath { get; }
    }

    private readonly struct FileDigest
    {
        internal FileDigest(string relativePath, long length, byte[] hash)
        {
            RelativePath = relativePath;
            Length = length;
            Hash = hash;
        }

        internal string RelativePath { get; }
        internal long Length { get; }
        internal byte[] Hash { get; }
    }

    private enum DirectoryProbe { Missing, Available, Unavailable }
}

internal sealed class ActiveModInventorySnapshot
{
    internal ActiveModInventorySnapshot(LiveModInfo[] mods, bool truncated)
    {
        Mods = mods;
        Truncated = truncated;
    }

    internal LiveModInfo[] Mods { get; }
    internal bool Truncated { get; }
}
