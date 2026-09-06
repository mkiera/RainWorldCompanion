using System.IO.Compression;

namespace RainWorldCompanion.Core.CompanionMods;

public sealed class CompanionModManager
{
    private readonly Func<bool> _isGameRunning;
    private readonly SemaphoreSlim _operation = new(1, 1);
    internal Action? AfterPreviousMoved { get; set; }

    public CompanionModManager(string gameInstallPath, Func<bool> isGameRunning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameInstallPath);
        InstallFolder = Path.GetFullPath(Path.Combine(gameInstallPath, "RainWorld_Data", "StreamingAssets", "mods", "rwcompanion"));
        _isGameRunning = isGameRunning;
    }

    public string InstallFolder { get; }
    public string PreviousFolder => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(InstallFolder))!, ".rwcompanion-update", "previous");
    private string StageFolder => Path.Combine(Path.GetDirectoryName(PreviousFolder)!, "staging");

    public CompanionModStatus Inspect(bool enabled, string appVersion, int protocolVersion)
    {
        if (!Directory.Exists(InstallFolder))
            return new(false, enabled, false, null, "rwcompanion is not installed.");
        try
        {
            RejectLinks(InstallFolder);
            var manifest = CompanionModPackage.ReadManifest(InstallFolder);
            CompanionModPackage.ValidateFolder(InstallFolder, manifest);
            var compatible = manifest.IsCompatible(appVersion, protocolVersion);
            return new(true, enabled, compatible, manifest.Version,
                !compatible ? "Update Companion or repair the mod to use a compatible version." : !enabled ? "rwcompanion is disabled." : null);
        }
        catch (Exception error) when (Expected(error))
        {
            return new(true, enabled, false, null, "The installed companion mod needs repair: " + error.Message);
        }
    }

    public async Task<CompanionModInstallResult> InstallAsync(string packageZipPath, string expectedSha256,
        string appVersion, int protocolVersion, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Install(packageZipPath, expectedSha256, appVersion, protocolVersion, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }

    private CompanionModInstallResult Install(string zipPath, string expectedHash, string appVersion, int protocol, CancellationToken cancellationToken)
    {
        if (_isGameRunning()) return new(CompanionModInstallOutcome.Deferred, null, "Close Rain World to install the companion mod.");
        var movedPrevious = false;
        try
        {
            RejectLinks(Path.GetDirectoryName(InstallFolder)!, false);
            RejectLinks(InstallFolder);
            Recover();
            if (!CompanionModPackage.Hash(zipPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The companion mod download failed verification.");
            RejectLinks(StageFolder, false);
            RemoveOwnedFolder(StageFolder);
            Directory.CreateDirectory(StageFolder);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                long total = 0;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (archive.Entries.Count > 120) throw new InvalidDataException("The companion mod package contains too many files.");
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/')) continue;
                    var path = CompanionModPackage.ContainedPath(StageFolder, entry.FullName);
                    total += entry.Length;
                    if (total > 32 * 1024 * 1024 || !paths.Add(path) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("The companion mod package contains unsupported entries.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path);
                }
            }
            var manifest = CompanionModPackage.ReadManifest(StageFolder);
            CompanionModPackage.ValidateFolder(StageFolder, manifest);
            if (!manifest.IsCompatible(appVersion, protocol)) throw new InvalidDataException("The companion mod package is incompatible with this Companion version.");
            var expectedFiles = manifest.Files.Keys.Append(CompanionModPackage.ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.EnumerateFiles(StageFolder, "*", SearchOption.AllDirectories)
                .Any(path => !expectedFiles.Contains(Path.GetRelativePath(StageFolder, path).Replace('\\', '/'))))
                throw new InvalidDataException("The companion mod package contains unlisted files.");
            cancellationToken.ThrowIfCancellationRequested();
            if (_isGameRunning()) return new(CompanionModInstallOutcome.Deferred, manifest.Version, "The update is ready and will install after Rain World closes.");
            RemoveOwnedFolder(PreviousFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(InstallFolder)!);
            if (Directory.Exists(InstallFolder))
            {
                Directory.Move(InstallFolder, PreviousFolder);
                movedPrevious = true;
            }
            AfterPreviousMoved?.Invoke();
            Directory.Move(StageFolder, InstallFolder);
            return new(CompanionModInstallOutcome.Installed, manifest.Version, null);
        }
        catch (Exception error) when (Expected(error))
        {
            if (movedPrevious && !Directory.Exists(InstallFolder) && Directory.Exists(PreviousFolder))
            {
                try { Directory.Move(PreviousFolder, InstallFolder); }
                catch (Exception restoreError) when (Expected(restoreError))
                { return new(CompanionModInstallOutcome.Failed, null, error.Message + " The previous mod remains in " + PreviousFolder + ": " + restoreError.Message); }
            }
            return new(CompanionModInstallOutcome.Failed, null, error.Message);
        }
    }

    public void Recover()
    {
        if (_isGameRunning()) return;
        RejectLinks(Path.GetDirectoryName(InstallFolder)!, false);
        if (!Directory.Exists(InstallFolder) && Directory.Exists(PreviousFolder))
        {
            RejectLinks(PreviousFolder);
            var manifest = CompanionModPackage.ReadManifest(PreviousFolder);
            CompanionModPackage.ValidateFolder(PreviousFolder, manifest);
            Directory.Move(PreviousFolder, InstallFolder);
        }
    }

    private void RemoveOwnedFolder(string path)
    {
        if (path != StageFolder && path != PreviousFolder) throw new InvalidOperationException("Unexpected companion mod folder.");
        if (!Directory.Exists(path)) return;
        RejectLinks(path);
        Directory.Delete(path, true);
    }

    private static void RejectLinks(string path, bool descend = true)
    {
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Companion mod installation through linked folders is not supported.");
        if (!Directory.Exists(path) || !descend) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The companion mod folder contains a linked file or folder.");
            if ((attributes & FileAttributes.Directory) != 0) RejectLinks(entry);
        }
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or global::System.Text.Json.JsonException or KeyNotFoundException;
}
