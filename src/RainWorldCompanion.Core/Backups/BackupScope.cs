using System.Text.RegularExpressions;

namespace RainWorldCompanion.Core.Backups;

/// <summary>
/// One file inside the backup scope. RelativePath is always relative to the save root and always
/// uses a backslash separator.
/// </summary>
public sealed record ScopeEntry(string RelativePath, string FullPath, long Length, DateTime LastWriteUtc);

/// <summary>
/// The result of one walk of the save folder: the files that can be copied, and the in-scope
/// paths that were passed over because they are junctions or symlinks.
///
/// The two lists exist separately because <see cref="BackupScope.IsInScope(string)"/> answers from
/// the path alone and would accept the skipped ones. Without this, a save folder whose
/// dvrmentSaveStates is a junction produces an empty backup that reports success.
/// </summary>
public sealed record ScopeScan(IReadOnlyList<ScopeEntry> Files, IReadOnlyList<string> SkippedLinks);

/// <summary>
/// Decides which files under the Rain World save folder the manager is allowed to copy,
/// overwrite, or delete. Everything else in that folder is off limits.
///
/// <para>The rules are versioned, and every past version is still answerable. A restore makes the
/// in-scope part of the save folder match the snapshot, which means it deletes in-scope files the
/// snapshot does not contain, so widening the rules would otherwise widen what an old snapshot
/// deletes: a backup taken under version 1 holds no meadow.json, and restoring it under version 2
/// rules would read that as "the user deleted meadow.json" and remove it. Each snapshot records
/// the version it was taken under, and the restore asks
/// <see cref="IsInScope(string, int)"/> with that version before it deletes anything.</para>
///
/// <para>Adding a rule therefore means: raise <see cref="CurrentScopeVersion"/>, and gate the new rule
/// on the version rather than editing an older one. Removing a rule is different and needs no
/// gate. An exclusion can only ever make a restore delete fewer files, so the exclusions below
/// apply at every version.</para>
/// </summary>
public class BackupScope
{
    /// <summary>
    /// The rules as they were before online saves, Rain Meadow progression, RandomBuff save data
    /// and the wider mod config set were added. Snapshots written before the version was recorded
    /// are read as this one.
    /// </summary>
    public const int OriginalScopeVersion = 1;

    /// <summary>
    /// Version 2 adds meadow.json, the RandomBuff save files, every ModConfigs .txt rather than
    /// devourment.txt alone, and the dressmyslugcat, RandomBuff and Warp folders.
    /// </summary>
    public const int WiderModDataScopeVersion = 2;

    /// <summary>The version <see cref="Enumerate"/> and a new snapshot use.</summary>
    public const int CurrentScopeVersion = WiderModDataScopeVersion;

    /// <summary>
    /// Root-level save containers as of version 1, matched by exact anchored regex against the
    /// file NAME.
    ///
    /// The anchors are the point of this list. The live save folder contains "sav - Copy" and
    /// "sav - Copy (2)" alongside "sav", so a "sav*" glob would pull in 12 MB of somebody's
    /// manual copies, and a restore would then treat them as in-scope files to delete. Exact
    /// match keeps the scope to the files the game itself writes.
    /// </summary>
    private static readonly Regex[] OriginalRootFilePatterns =
    [
        new Regex("^sav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^sav[23]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^exp[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^expCore[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex("^online_sav[0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>
    /// Root-level files added in version 2. Anchored for the same reason as the list above.
    /// buffMain and buffsave are RandomBuff's own save data, which is progress the player cannot
    /// get back, not settings they can retype.
    ///
    /// The negative online container names are real. Rain Meadow's hook on
    /// Options.GetSaveFileName_SavOrExp returns "online_sav" + (saveSlot + 1) whenever saveSlot is
    /// not 0, and the base game uses a negative saveSlot for Expedition, so joining a lobby with
    /// saveSlot -2 writes online_sav-1. The pattern above covers online_sav0, which saveSlot -1
    /// produces. Neither is a menu slot, so the app never lists them as one, but a save is a save
    /// and it gets backed up.
    /// </summary>
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

    /// <summary>
    /// Steam's own sync manifest. It sits at the save root, inside ModConfigs and inside the SJ
    /// folders, and restoring a stale one tells the Steam client that files it has already synced
    /// are current. Excluded wherever it appears, including inside folders that are otherwise
    /// taken whole.
    /// </summary>
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

    private static readonly string[] CurrentRecursiveFolderList =
        [.. OriginalRecursiveFolderList, .. WiderModDataRecursiveFolderList];

    /// <summary>
    /// Folders whose top-level files are judged one at a time rather than taken whole. ModConfigs
    /// holds one .txt per mod next to folders that belong to individual mods, so the files are in
    /// scope by name and the folders are handled by their own rules.
    /// </summary>
    private static readonly string[] TopLevelFileFolderList = [ModConfigsFolder];

    /// <summary>
    /// The scope folders that are taken whole, relative to the save root. A restore may tidy up
    /// empty folders below these and nowhere else.
    /// </summary>
    public static IReadOnlyList<string> RecursiveFolders => CurrentRecursiveFolderList;

    /// <summary>
    /// The folders taken whole under one rules version, which is what a caller acting on behalf of
    /// a snapshot needs. Version 1 covered dvrmentSaveStates and ModConfigs\DvrmentConfs and
    /// nothing else, so a restore of a version 1 snapshot must not walk into dressmyslugcat,
    /// RandomBuff or Warp: those rules have no opinion about what is in there.
    ///
    /// A version above <see cref="CurrentScopeVersion"/>, which a snapshot from a newer build
    /// carries, gets today's list, the same way <see cref="IsInScope(string, int)"/> does.
    /// </summary>
    public static IReadOnlyList<string> RecursiveFoldersAt(int scopeVersion) =>
        scopeVersion >= WiderModDataScopeVersion ? CurrentRecursiveFolderList : OriginalRecursiveFolderList;

    public BackupScope(string saveRoot)
        : this(saveRoot, CurrentScopeVersion)
    {
    }

    /// <summary>
    /// A scope pinned to one rules version. Everything this instance enumerates and everything it
    /// calls in scope is judged under <paramref name="scopeVersion"/>, and a snapshot written
    /// through it records that version, so an older rule set can be reproduced exactly rather than
    /// being approximated by filtering the current one.
    /// </summary>
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

    /// <summary>The rules version this instance judges by, and the one a snapshot records.</summary>
    public int Version { get; }

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
        var skipped = new HashSet<string>(NameComparer);
        var seen = new HashSet<string>(NameComparer);

        if (!Directory.Exists(SaveRoot))
        {
            return new ScopeScan(results, Array.Empty<string>());
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

        foreach (var folder in TopLevelFileFolderList)
        {
            AddTopLevelFiles(results, skipped, seen, folder);
        }

        // The folder list is picked by this instance's version, not by today's. AddTree records
        // every reparse point it meets as a skipped link, and a version 1 scope walking Warp would
        // report a link skip for a tree its own rules never covered, which then lands in a version
        // 1 manifest and comes back as a warning on every restore of it.
        foreach (var folder in RecursiveFoldersAt(Version))
        {
            AddTree(results, skipped, seen, folder);
        }

        results.Sort(static (a, b) => NameComparer.Compare(a.RelativePath, b.RelativePath));

        var skippedLinks = skipped.ToList();
        skippedLinks.Sort(NameComparer);
        return new ScopeScan(results, skippedLinks);
    }

    /// <summary>
    /// Whether a path relative to the save root is in scope under this instance's rules version.
    /// Accepts either separator and answers from the path alone, without touching the disk.
    /// </summary>
    public bool IsInScope(string relativePath) => IsInScope(relativePath, Version);

    /// <summary>
    /// Whether a path was in scope under a given rules version. A version below
    /// <see cref="OriginalScopeVersion"/> reads as version 1, and a version above
    /// <see cref="CurrentScopeVersion"/>, which is what a snapshot from a newer build of this app
    /// carries, gets today's rules: this build can only judge the rules it knows.
    /// </summary>
    public bool IsInScope(string relativePath, int scopeVersion)
    {
        var normalised = Normalise(relativePath);
        return normalised is not null && MatchesRules(normalised, scopeVersion);
    }

    /// <summary>
    /// Today's rules in plain words, for the UI to list. What is included first, then what is
    /// left out and why.
    /// </summary>
    public static IReadOnlyList<string> DescribeRules() =>
    [
        "Save containers in the save folder itself: sav, sav2, sav3, exp<n>, expCore<n>",
        "Rain Meadow's online containers: online_sav<n>, and the online_sav-<n> form a lobby joined from an Expedition slot writes",
        "Rain Meadow character progression: meadow.json",
        "RandomBuff save data: buffMain<n> and buffsave<n>",
        @"Every .txt file directly inside ModConfigs\, which is where mods keep their settings",
        @"dvrmentSaveStates\, ModConfigs\DvrmentConfs\, dressmyslugcat\, RandomBuff\ and Warp\, with everything inside them at any depth",
        "steam_autocloud.vdf is left out wherever it sits, because it is Steam's own sync manifest",
        "Matched on exact file names, so copies such as \"sav - Copy\" are left alone",
        @"Everything else is excluded: options and localoptions.txt are game settings rather than save data, SJ_0 to SJ_2 are karma screenshots the game redraws by itself, and backup\ and cloud\ belong to the game and to Steam",
    ];

    /// <summary>
    /// Whether a path is one the rules of <paramref name="scopeVersion"/> would have taken but an
    /// exclusion added since then rejects.
    ///
    /// Exclusions are not version gated, because leaving a file out can only make a restore delete
    /// fewer files. It also makes a restore put back fewer files, and that is what this answers:
    /// a snapshot written before steam_autocloud.vdf was excluded holds one, and the restore has to
    /// tell "the manifest names a file the rules no longer cover" apart from "the manifest names a
    /// path this app never managed", which is a broken manifest.
    /// </summary>
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

    /// <summary>
    /// The single rule set behind both <see cref="Enumerate"/> and <see cref="IsInScope(string)"/>.
    /// The first argument is an already normalised relative path: backslash separators, no leading
    /// separator, no "." or ".." segments.
    /// </summary>
    private static bool MatchesRules(string normalisedRelativePath, int scopeVersion)
    {
        var segments = normalisedRelativePath.Split('\\');
        return !IsExcluded(segments) && MatchesInclusionRules(segments, scopeVersion);
    }

    /// <summary>
    /// What is left out whatever the rules version says. Kept apart from the inclusion rules
    /// because <see cref="IsExcludedSinceScopeVersion"/> needs the two answers separately.
    /// </summary>
    private static bool IsExcluded(string[] segments) =>
        NameComparer.Equals(segments[^1], SteamCloudManifestFile);

    private static bool MatchesInclusionRules(string[] segments, int scopeVersion)
    {
        if (MatchesOriginalRules(segments))
        {
            return true;
        }

        return scopeVersion >= WiderModDataScopeVersion && MatchesWiderModDataRules(segments);
    }

    /// <summary>
    /// The rules exactly as they were at version 1. Frozen: a change here changes what restoring
    /// an old snapshot is allowed to delete.
    /// </summary>
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

    /// <summary>
    /// What version 2 adds on top. Every rule here is an addition, so version 2 covers everything
    /// version 1 covered.
    /// </summary>
    private static bool MatchesWiderModDataRules(string[] segments)
    {
        if (segments.Length == 1)
        {
            return MatchesAny(WiderModDataRootFilePatterns, segments[0]);
        }

        // Mod settings sit one per .txt directly in ModConfigs. Subfolders there belong to
        // individual mods and are in scope only when they are named in the recursive list.
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
    /// Adds the in-scope files sitting directly inside one folder, without descending into it.
    /// </summary>
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

    /// <summary>
    /// Walks one of the recursive scope folders. The walk is manual so reparse points can be
    /// skipped: a junction or symlink inside the save folder would otherwise let enumeration
    /// escape the root, and a restore would then write through it. Every skip is recorded
    /// rather than swallowed, because a whole junctioned folder would otherwise read as a
    /// folder with nothing in it.
    /// </summary>
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

    /// <summary>
    /// Resolves one of the scope folders and reports whether the walk may go into it.
    ///
    /// Every folder on the way down is checked, not only the last one. ModConfigs\DvrmentConfs is
    /// two levels deep, and Directory.Exists follows a junction silently, so a junctioned
    /// ModConfigs would otherwise let the walk into DvrmentConfs on the far side of it.
    /// A folder that is simply not there is no skip, because there is nothing to copy either way.
    /// </summary>
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
