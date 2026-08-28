using System.Text.RegularExpressions;

namespace RainWorldCompanion.Core.Backups;

/// <summary>RelativePath is relative to the save root and always uses a backslash separator.</summary>
public sealed record ScopeEntry(string RelativePath, string FullPath, long Length, DateTime LastWriteUtc);

/// <summary>SkippedLinks is separate from Files because a junctioned folder would otherwise
/// produce an empty backup that reports success.</summary>
public sealed record ScopeScan(IReadOnlyList<ScopeEntry> Files, IReadOnlyList<string> SkippedLinks);

/// <summary>
/// Which files under the Rain World save folder may be copied, overwritten, or deleted. Adding a
/// rule means raising <see cref="CurrentScopeVersion"/> and gating it on the version rather than
/// editing an older one, or an old snapshot's restore starts deleting under today's wider rules.
/// </summary>
public class BackupScope
{
    /// <summary>Snapshots written before the version was recorded are read as this one.</summary>
    public const int OriginalScopeVersion = 1;

    public const int WiderModDataScopeVersion = 2;

    /// <summary>Takes ModConfigs whole rather than its .txt files and DvrmentConfs alone, so a mod
    /// that keeps its settings in a folder or a .json is covered too.</summary>
    public const int WholeModConfigsScopeVersion = 3;

    public const int CurrentScopeVersion = WholeModConfigsScopeVersion;

    /// <summary>Anchored on purpose: the live save folder holds "sav - Copy" next to "sav", and a
    /// "sav*" glob would put those in scope for a restore to delete.</summary>
    private static readonly Regex[] OriginalRootFilePatterns =
    [
        new Regex("^sav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^sav[23]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^exp[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^expCore[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^online_sav[0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>The negative online container names are real: Rain Meadow names the file
    /// "online_sav" + (saveSlot + 1) and the base game uses a negative saveSlot for Expedition, so
    /// joining a lobby from saveSlot -2 writes online_sav-1.</summary>
    private static readonly Regex[] WiderModDataRootFilePatterns =
    [
        new Regex(@"^meadow\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^buffMain[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^buffsave[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^online_sav-[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    private const string ModConfigsFolder = "ModConfigs";
    private const string DevourmentConfigFile = "devourment.txt";
    private const string DevourmentSaveStatesFolder = "dvrmentSaveStates";
    private const string DevourmentConfsFolder = "DvrmentConfs";
    private const string ConfigFileExtension = ".txt";

    /// <summary>Steam's own sync manifest. Restoring a stale one tells the Steam client that files
    /// it has already synced are current.</summary>
    private const string SteamCloudManifestFile = "steam_autocloud.vdf";

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly string[] OriginalRecursiveFolderList =
    [
        DevourmentSaveStatesFolder,
        ModConfigsFolder + "\\" + DevourmentConfsFolder,
    ];

    private static readonly string[] WiderModDataRecursiveFolderList =
    [
        "dressmyslugcat",
        "RandomBuff",
        "Warp",
    ];

    /// <summary>ModConfigs\DvrmentConfs stays in the version 1 list above rather than being folded
    /// into this one, because version 1's rules are frozen. A version 3 walk therefore reaches those
    /// files from two roots, and the seen set in <see cref="Scan"/> drops the second sighting.</summary>
    private static readonly string[] WholeModConfigsRecursiveFolderList = [ModConfigsFolder];

    private static readonly string[] VersionTwoRecursiveFolderList =
        [.. OriginalRecursiveFolderList, .. WiderModDataRecursiveFolderList];

    private static readonly string[] CurrentRecursiveFolderList =
        [.. VersionTwoRecursiveFolderList, .. WholeModConfigsRecursiveFolderList];

    /// <summary>Walked for their files alone, without descending. Empty from version 3 on, where
    /// ModConfigs moved into the recursive list: walking it in both would walk it twice.</summary>
    private static readonly string[] OriginalTopLevelFileFolderList = [ModConfigsFolder];

    public static IReadOnlyList<string> RecursiveFolders => CurrentRecursiveFolderList;

    /// <summary>The folders taken whole under one rules version. A version above
    /// <see cref="CurrentScopeVersion"/> gets today's list, as <see cref="IsInScope(string, int)"/> does.</summary>
    public static IReadOnlyList<string> RecursiveFoldersAt(int scopeVersion) =>
        scopeVersion >= WholeModConfigsScopeVersion ? CurrentRecursiveFolderList
        : scopeVersion >= WiderModDataScopeVersion ? VersionTwoRecursiveFolderList
        : OriginalRecursiveFolderList;

    private static IReadOnlyList<string> TopLevelFileFoldersAt(int scopeVersion) =>
        scopeVersion >= WholeModConfigsScopeVersion
            ? Array.Empty<string>()
            : OriginalTopLevelFileFolderList;

    public BackupScope(string saveRoot)
        : this(saveRoot, CurrentScopeVersion)
    {
    }

    public BackupScope(string saveRoot, int scopeVersion)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            throw new ArgumentException("Save root must not be empty.", nameof(saveRoot));
        }

        SaveRoot = Path.GetFullPath(saveRoot);
        Version = scopeVersion;
    }

    public string SaveRoot { get; }

    public int Version { get; }

    /// <summary>Every in-scope file that currently exists on disk, sorted by relative path.</summary>
    public IReadOnlyList<ScopeEntry> Enumerate() => Scan().Files;

    public virtual ScopeScan Scan()
    {
        var results = new List<ScopeEntry>();
        var skipped = new HashSet<string>(NameComparer);
        var seen = new HashSet<string>(NameComparer);

        if (!Directory.Exists(SaveRoot))
        {
            return new ScopeScan(results, Array.Empty<string>());
        }

        foreach (var fullPath in Directory.EnumerateFiles(SaveRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(fullPath);

            if (IsInScope(name))
            {
                TryAdd(results, skipped, seen, fullPath, name);
            }
        }

        // Both lists come from this instance's version, not today's, for the reason the recursive
        // loop below gives. ModConfigs moves from one list to the other at version 3.
        foreach (var folder in TopLevelFileFoldersAt(Version))
        {
            AddTopLevelFiles(results, skipped, seen, folder);
        }

        // The folder list comes from this instance's version, not today's: a version 1 scope
        // walking Warp would record a link skip for a tree its own rules never covered.
        foreach (var folder in RecursiveFoldersAt(Version))
        {
            AddTree(results, skipped, seen, folder);
        }

        results.Sort(static (a, b) => NameComparer.Compare(a.RelativePath, b.RelativePath));

        var skippedLinks = skipped.ToList();
        skippedLinks.Sort(NameComparer);
        return new ScopeScan(results, skippedLinks);
    }

    /// <summary>Answers from the path alone, without touching the disk. Accepts either separator.</summary>
    public bool IsInScope(string relativePath) => IsInScope(relativePath, Version);

    /// <summary>A version below <see cref="OriginalScopeVersion"/> reads as version 1, and one above
    /// <see cref="CurrentScopeVersion"/> gets today's rules.</summary>
    public bool IsInScope(string relativePath, int scopeVersion)
    {
        var normalised = Normalise(relativePath);
        return normalised is not null && MatchesRules(normalised, scopeVersion);
    }

    public static IReadOnlyList<string> DescribeRules() =>
    [
        "Save containers in the save folder itself: sav, sav2, sav3, exp<n>, expCore<n>",
        "Rain Meadow's online containers: online_sav<n>, and the online_sav-<n> form a lobby joined from an Expedition slot writes",
        "Rain Meadow character progression: meadow.json",
        "RandomBuff save data: buffMain<n> and buffsave<n>",
        @"ModConfigs\, which is where mods keep their settings, with everything inside it at any depth",
        @"dvrmentSaveStates\, dressmyslugcat\, RandomBuff\ and Warp\, with everything inside them at any depth",
        "steam_autocloud.vdf is left out wherever it sits, because it is Steam's own sync manifest",
        "Matched on exact file names, so copies such as \"sav - Copy\" are left alone",
        @"Everything else is excluded: options and localoptions.txt are game settings rather than save data, SJ_0 to SJ_2 are karma screenshots the game redraws by itself, and backup\ and cloud\ belong to the game and to Steam",
    ];

    /// <summary>Whether a path is one the rules of <paramref name="scopeVersion"/> would have taken
    /// but an exclusion added since then rejects. This is what tells "the rules no longer cover this
    /// file" apart from a manifest naming a path the app never managed, which is a broken manifest.</summary>
    public bool IsExcludedSinceScopeVersion(string relativePath, int scopeVersion)
    {
        var normalised = Normalise(relativePath);
        if (normalised is null)
        {
            return false;
        }

        var segments = normalised.Split('\\');
        return IsExcluded(segments) && MatchesInclusionRules(segments, scopeVersion);
    }

    /// <summary>Takes an already normalised relative path: backslash separators, no leading
    /// separator, no "." or ".." segments.</summary>
    private static bool MatchesRules(string normalisedRelativePath, int scopeVersion)
    {
        var segments = normalisedRelativePath.Split('\\');
        return !IsExcluded(segments) && MatchesInclusionRules(segments, scopeVersion);
    }

    private static bool IsExcluded(string[] segments) =>
        NameComparer.Equals(segments[^1], SteamCloudManifestFile);

    private static bool MatchesInclusionRules(string[] segments, int scopeVersion)
    {
        if (MatchesOriginalRules(segments))
        {
            return true;
        }

        if (scopeVersion >= WiderModDataScopeVersion && MatchesWiderModDataRules(segments))
        {
            return true;
        }

        return scopeVersion >= WholeModConfigsScopeVersion && MatchesWholeModConfigsRules(segments);
    }

    /// <summary>Everything under ModConfigs at any depth, which is where mods keep their settings.
    /// The version 1 and version 2 rules above still name their own corners of it, and are left
    /// alone: what an old snapshot may delete is decided by the rules it was taken under.</summary>
    private static bool MatchesWholeModConfigsRules(string[] segments) =>
        segments.Length >= 2 && NameComparer.Equals(segments[0], ModConfigsFolder);

    /// <summary>The rules exactly as they were at version 1. Frozen: a change here changes what
    /// restoring an old snapshot is allowed to delete.</summary>
    private static bool MatchesOriginalRules(string[] segments)
    {
        if (segments.Length == 1)
        {
            return MatchesAny(OriginalRootFilePatterns, segments[0]);
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

        return segments.Length >= 3
            && NameComparer.Equals(segments[0], ModConfigsFolder)
            && NameComparer.Equals(segments[1], DevourmentConfsFolder);
    }

    private static bool MatchesWiderModDataRules(string[] segments)
    {
        if (segments.Length == 1)
        {
            return MatchesAny(WiderModDataRootFilePatterns, segments[0]);
        }

        if (segments.Length == 2
            && NameComparer.Equals(segments[0], ModConfigsFolder)
            && NameComparer.Equals(Path.GetExtension(segments[1]), ConfigFileExtension))
        {
            return true;
        }

        foreach (var folder in WiderModDataRecursiveFolderList)
        {
            if (NameComparer.Equals(segments[0], folder))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAny(Regex[] patterns, string name)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns null for anything that is not a relative path inside the root.</summary>
    private static string? Normalise(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var text = relativePath.Replace('/', '\\');

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

            // Windows drops trailing spaces and dots when it resolves a path, so trimming a segment
            // here would let a file named "sav " pass the exact match and be written over "sav".
            kept.Add(segment);
        }

        return kept.Count == 0 ? null : string.Join('\\', kept);
    }

    private void AddTopLevelFiles(
        List<ScopeEntry> results,
        HashSet<string> skipped,
        HashSet<string> seen,
        string relativeFolder)
    {
        if (!TryEnterFolder(relativeFolder, skipped, out var fullPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly))
        {
            var relative = Path.Combine(relativeFolder, Path.GetFileName(file));
            if (IsInScope(relative))
            {
                TryAdd(results, skipped, seen, file, relative);
            }
        }
    }

    /// <summary>The walk is manual so reparse points can be skipped: a junction inside the save
    /// folder would let enumeration escape the root, and a restore would then write through it.</summary>
    private void AddTree(List<ScopeEntry> results, HashSet<string> skipped, HashSet<string> seen, string relativeFolder)
    {
        if (!TryEnterFolder(relativeFolder, skipped, out var rootPath))
        {
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

    /// <summary>Every folder on the way down is checked, not only the last: Directory.Exists follows
    /// a junction silently, so a junctioned ModConfigs would let the walk into DvrmentConfs behind it.</summary>
    private bool TryEnterFolder(string relativeFolder, HashSet<string> skipped, out string fullPath)
    {
        fullPath = Path.Combine(SaveRoot, relativeFolder);

        var walked = SaveRoot;
        var relative = "";

        foreach (var segment in relativeFolder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Path.Combine(walked, segment);
            relative = relative.Length == 0 ? segment : relative + "\\" + segment;

            if (!Directory.Exists(walked))
            {
                return false;
            }

            if (IsReparsePoint(walked))
            {
                skipped.Add(relative);
                return false;
            }
        }

        return true;
    }

    private static void TryAdd(
        List<ScopeEntry> results,
        HashSet<string> skipped,
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
