using System.Text.RegularExpressions;

namespace RainWorldSaveManager.Core.Backups;

/// <summary>
/// One file inside the backup scope. RelativePath is always relative to the save root and always
/// uses a backslash separator.
/// </summary>
public sealed record ScopeEntry(string RelativePath, string FullPath, long Length, DateTime LastWriteUtc);

/// <summary>
/// The result of one walk of the save folder: the files that can be copied, and the in-scope
/// paths that were passed over because they are junctions or symlinks.
///
/// The two lists exist separately because <see cref="BackupScope.IsInScope"/> answers from the
/// path alone and would accept the skipped ones. Without this, a save folder whose
/// dvrmentSaveStates is a junction produces an empty backup that reports success.
/// </summary>
public sealed record ScopeScan(IReadOnlyList<ScopeEntry> Files, IReadOnlyList<string> SkippedLinks);

/// <summary>
/// Decides which files under the Rain World save folder the manager is allowed to copy,
/// overwrite, or delete. Everything else in that folder is off limits.
/// </summary>
public class BackupScope
{
    /// <summary>
    /// Root-level save containers, matched by exact anchored regex against the file NAME.
    ///
    /// The anchors are the point of this list. The live save folder contains "sav - Copy" and
    /// "sav - Copy (2)" alongside "sav", so a "sav*" glob would pull in 12 MB of somebody's
    /// manual copies, and a restore would then treat them as in-scope files to delete. Exact
    /// match keeps the scope to the files the game itself writes.
    /// </summary>
    private static readonly Regex[] RootFilePatterns =
    [
        new Regex("^sav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^sav[23]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^exp[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^expCore[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^online_sav[0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    private const string ModConfigsFolder = "ModConfigs";
    private const string DevourmentConfigFile = "devourment.txt";
    private const string DevourmentSaveStatesFolder = "dvrmentSaveStates";
    private const string DevourmentConfsFolder = "DvrmentConfs";

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly string[] RecursiveFolderList =
    [
        DevourmentSaveStatesFolder,
        ModConfigsFolder + "\\" + DevourmentConfsFolder,
    ];

    /// <summary>
    /// The scope folders that are taken whole, relative to the save root. A restore may tidy up
    /// empty folders below these and nowhere else.
    /// </summary>
    public static IReadOnlyList<string> RecursiveFolders => RecursiveFolderList;

    public BackupScope(string saveRoot)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            throw new ArgumentException("Save root must not be empty.", nameof(saveRoot));
        }

        SaveRoot = Path.GetFullPath(saveRoot);
    }

    public string SaveRoot { get; }

    /// <summary>
    /// Every in-scope file that currently exists on disk, sorted by relative path.
    /// </summary>
    public IReadOnlyList<ScopeEntry> Enumerate() => Scan().Files;

    /// <summary>
    /// One walk of the save folder, returning both the files that can be copied and the
    /// in-scope paths that were passed over because they are links.
    /// </summary>
    public virtual ScopeScan Scan()
    {
        var results = new List<ScopeEntry>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(NameComparer);

        if (!Directory.Exists(SaveRoot))
        {
            return new ScopeScan(results, skipped);
        }

        foreach (var fullPath in Directory.EnumerateFiles(SaveRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(fullPath);

            // Enumerate asks IsInScope rather than the rules directly, so the two answers are the
            // same answer by construction.
            if (IsInScope(name))
            {
                TryAdd(results, skipped, seen, fullPath, name);
            }
        }

        var devourmentConfig = Path.Combine(SaveRoot, ModConfigsFolder, DevourmentConfigFile);
        if (File.Exists(devourmentConfig))
        {
            TryAdd(results, skipped, seen, devourmentConfig, Path.Combine(ModConfigsFolder, DevourmentConfigFile));
        }

        foreach (var folder in RecursiveFolderList)
        {
            AddTree(results, skipped, seen, folder);
        }

        results.Sort(static (a, b) => NameComparer.Compare(a.RelativePath, b.RelativePath));
        skipped.Sort(NameComparer);
        return new ScopeScan(results, skipped);
    }

    /// <summary>
    /// Whether a path relative to the save root is in scope. Accepts either separator and
    /// answers from the path alone, without touching the disk.
    /// </summary>
    public bool IsInScope(string relativePath)
    {
        var normalised = Normalise(relativePath);
        return normalised is not null && MatchesRules(normalised);
    }

    /// <summary>
    /// The scope rules in the order they are applied, for display in the UI.
    /// </summary>
    public static IReadOnlyList<string> DescribeRules() =>
    [
        "Save containers in the save folder itself: sav, sav2, sav3, exp<n>, expCore<n>, online_sav<n>",
        @"ModConfigs\devourment.txt",
        @"dvrmentSaveStates\ and everything inside it, at any depth",
        @"ModConfigs\DvrmentConfs\ and everything inside it, at any depth",
        "Matched on exact file names, so copies such as \"sav - Copy\" are left alone",
        @"Everything else is excluded, including options, the game's own backup\ and cloud\ folders, and other mods' configs",
    ];

    /// <summary>
    /// The single rule set behind both <see cref="Enumerate"/> and <see cref="IsInScope"/>.
    /// The argument is an already normalised relative path: backslash separators, no leading
    /// separator, no "." or ".." segments.
    /// </summary>
    private static bool MatchesRules(string normalisedRelativePath)
    {
        var segments = normalisedRelativePath.Split('\\');

        if (segments.Length == 1)
        {
            var name = segments[0];
            foreach (var pattern in RootFilePatterns)
            {
                if (pattern.IsMatch(name))
                {
                    return true;
                }
            }

            return false;
        }

        if (segments.Length == 2
            && NameComparer.Equals(segments[0], ModConfigsFolder)
            && NameComparer.Equals(segments[1], DevourmentConfigFile))
        {
            return true;
        }

        if (NameComparer.Equals(segments[0], DevourmentSaveStatesFolder))
        {
            return true;
        }

        if (segments.Length >= 3
            && NameComparer.Equals(segments[0], ModConfigsFolder)
            && NameComparer.Equals(segments[1], DevourmentConfsFolder))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reduces a caller-supplied relative path to the form <see cref="MatchesRules"/> expects.
    /// Returns null for anything that is not a relative path inside the root, which then reads
    /// as out of scope.
    /// </summary>
    private static string? Normalise(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var text = relativePath.Replace('/', '\\');

        // A rooted path is not a path relative to the save root, whatever it points at.
        if (Path.IsPathRooted(text))
        {
            return null;
        }

        var segments = text.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return null;
            }

            // Segments are compared as they are, never trimmed. Windows drops trailing spaces and
            // dots when it resolves a path, so trimming here would let a file named "sav " pass
            // the exact-match rule and then be written over the real "sav".
            kept.Add(segment);
        }

        return kept.Count == 0 ? null : string.Join('\\', kept);
    }

    /// <summary>
    /// Walks one of the recursive scope folders. The walk is manual so reparse points can be
    /// skipped: a junction or symlink inside the save folder would otherwise let enumeration
    /// escape the root, and a restore would then write through it. Every skip is recorded
    /// rather than swallowed, because a whole junctioned folder would otherwise read as a
    /// folder with nothing in it.
    /// </summary>
    private void AddTree(List<ScopeEntry> results, List<string> skipped, HashSet<string> seen, string relativeFolder)
    {
        var rootPath = Path.Combine(SaveRoot, relativeFolder);
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        if (IsReparsePoint(rootPath))
        {
            skipped.Add(relativeFolder);
            return;
        }

        var pending = new Stack<(string FullPath, string RelativePath)>();
        pending.Push((rootPath, relativeFolder));

        while (pending.Count > 0)
        {
            var (directory, relativeDirectory) = pending.Pop();

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.Combine(relativeDirectory, Path.GetFileName(file));
                if (IsInScope(relative))
                {
                    TryAdd(results, skipped, seen, file, relative);
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var relativeChild = Path.Combine(relativeDirectory, Path.GetFileName(child));

                if (IsReparsePoint(child))
                {
                    skipped.Add(relativeChild);
                    continue;
                }

                pending.Push((child, relativeChild));
            }
        }
    }

    private static void TryAdd(
        List<ScopeEntry> results,
        List<string> skipped,
        HashSet<string> seen,
        string fullPath,
        string relativePath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return;
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                skipped.Add(relativePath);
                return;
            }

            if (seen.Add(relativePath))
            {
                results.Add(new ScopeEntry(relativePath, info.FullName, info.Length, info.LastWriteTimeUtc));
            }
        }
        catch (FileNotFoundException)
        {
            // The file went away between listing and reading it. Nothing to back up.
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
