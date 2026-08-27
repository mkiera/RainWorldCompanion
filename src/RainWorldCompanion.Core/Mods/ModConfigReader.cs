// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Mods;

/// <summary>One settings file found on disk. The hash is settled by whoever copies it.</summary>
public sealed record ModConfigEntry(string RelativePath, string FullPath, string ModId, long Length);

/// <summary>
/// What one look at a folder's mod settings found.
/// </summary>
/// <param name="ReadTheFolder">
/// False means the three fields below say nothing. An empty <paramref name="Files"/> with this
/// true is a real answer: a folder can hold no mod settings at all.
/// </param>
/// <param name="SkippedLinks">
/// Separate from <paramref name="Files"/> for the reason <see cref="Backups.ScopeScan"/>'s is: a
/// junctioned DvrmentConfs would otherwise produce a capture that is silently empty.
/// </param>
public sealed record ModConfigScan(
    IReadOnlyList<ModConfigEntry> Files,
    IReadOnlyList<string> SkippedLinks,
    bool ReadTheFolder,
    string? Note)
{
    public static ModConfigScan NothingRead(string? note) => new(
        Array.Empty<ModConfigEntry>(),
        Array.Empty<string>(),
        false,
        note);
}

/// <summary>
/// Finds the mod settings that travel with a save.
///
/// <para>Remix writes every mod's settings to ModConfigs\&lt;mod id&gt;.txt in the save folder,
/// keyed by the id in modinfo.json. That is the id <see cref="ModEntry.Id"/> holds, so a settings
/// file joins to a recorded mod, and it works for a mod this app has never heard of.</para>
///
/// <para><see cref="Travels"/> is deliberately narrower than
/// <see cref="Backups.BackupScope"/>. The scope is what this app may overwrite and delete inside
/// the player's own folder. This is what is safe to hand to a stranger, so it takes the shapes the
/// game itself writes and leaves a mod's arbitrary subfolder alone.</para>
///
/// <para>Nothing here is written. The app never edits the game's own files.</para>
/// </summary>
public static class ModConfigReader
{
    /// <summary>Where Remix keeps mod settings, relative to the save folder.</summary>
    public const string ModConfigsFolderName = "ModConfigs";

    /// <summary>Steam's own sync manifest, which carries an account id and never travels.</summary>
    public const string SteamCloudManifestFile = "steam_autocloud.vdf";

    /// <summary>
    /// The one folder a mod keeps inside ModConfigs. Devourment builds it from the same static
    /// directory path Remix uses, so it sits beside the .txt files rather than under the mod.
    /// </summary>
    public const string DevourmentConfsFolder = "DvrmentConfs";

    /// <summary>The mod that owns <see cref="DevourmentConfsFolder"/>, whose name does not say so.</summary>
    public const string DevourmentModId = "devourment";

    private const string ConfigFileExtension = ".txt";

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Never throws. A folder that cannot be listed costs the answer rather than the caller.
    /// </summary>
    public static ModConfigScan Read(string? saveRoot)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            return ModConfigScan.NothingRead(
                "The save folder is not known, so the mod settings could not be read.");
        }

        string root;
        string configs;
        try
        {
            root = Path.GetFullPath(saveRoot);
            configs = Path.Combine(root, ModConfigsFolderName);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return ModConfigScan.NothingRead(
                "The save folder path is not usable, so the mod settings could not be read.");
        }

        if (!DirectoryExistsSafe(configs))
        {
            // A folder with no ModConfigs is a real answer: the player has no mod settings. That
            // is not the same as a folder we could not look at.
            return new ModConfigScan(Array.Empty<ModConfigEntry>(), Array.Empty<string>(), true, null);
        }

        if (CanonicalPath.IsLink(configs))
        {
            return new ModConfigScan(
                Array.Empty<ModConfigEntry>(),
                new[] { ModConfigsFolderName },
                true,
                "ModConfigs is a link, so the mod settings behind it were left alone.");
        }

        var files = new List<ModConfigEntry>();
        var skipped = new List<string>();

        Walk(root, configs, ModConfigsFolderName, files, skipped);

        files.Sort(static (a, b) => NameComparer.Compare(a.RelativePath, b.RelativePath));
        skipped.Sort(NameComparer);

        return new ModConfigScan(
            files,
            skipped,
            true,
            skipped.Count == 0
                ? null
                : "Some mod settings were left alone because they are links: " + string.Join(", ", skipped) + ".");
    }

    /// <summary>
    /// Whether a settings file is one that travels with a save. Answers from the path alone.
    /// </summary>
    public static bool Travels(string? relativePath)
    {
        string[]? segments = Split(relativePath);
        if (segments is null || segments.Length < 2)
        {
            return false;
        }

        if (!NameComparer.Equals(segments[0], ModConfigsFolderName))
        {
            return false;
        }

        // Never, at any depth: it names the Steam account the folder belongs to.
        if (NameComparer.Equals(segments[^1], SteamCloudManifestFile))
        {
            return false;
        }

        if (segments.Length == 2)
        {
            return NameComparer.Equals(Path.GetExtension(segments[1]), ConfigFileExtension);
        }

        return NameComparer.Equals(segments[1], DevourmentConfsFolder);
    }

    /// <summary>
    /// The mod a settings file belongs to, or empty when it cannot be worked out.
    ///
    /// <para>Remix writes ModConfigs\&lt;mod id&gt;.txt, so the file name without its extension is
    /// the id. DvrmentConfs is the one folder that has to be named here: it is Devourment's, and
    /// nothing about the name says so.</para>
    /// </summary>
    public static string ModIdFor(string? relativePath)
    {
        string[]? segments = Split(relativePath);
        if (segments is null || segments.Length < 2 || !NameComparer.Equals(segments[0], ModConfigsFolderName))
        {
            return "";
        }

        if (segments.Length >= 3)
        {
            return NameComparer.Equals(segments[1], DevourmentConfsFolder) ? DevourmentModId : "";
        }

        return Path.GetFileNameWithoutExtension(segments[1]);
    }

    /// <summary>
    /// Walks by hand rather than with a recursive enumeration, so a junction can be skipped: a
    /// link inside ModConfigs would otherwise pull files from anywhere on the machine into a
    /// bundle the player then sends to somebody else.
    /// </summary>
    private static void Walk(
        string root,
        string folder,
        string relativeFolder,
        List<ModConfigEntry> files,
        List<string> skipped)
    {
        string[]? entries = ListFiles(folder);
        if (entries is not null)
        {
            foreach (string full in entries)
            {
                string relative = Path.Combine(relativeFolder, Path.GetFileName(full));
                if (!Travels(relative))
                {
                    continue;
                }

                if (CanonicalPath.IsLink(full))
                {
                    skipped.Add(relative);
                    continue;
                }

                long length = LengthOf(full);
                if (length >= 0)
                {
                    files.Add(new ModConfigEntry(relative, full, ModIdFor(relative), length));
                }
            }
        }

        string[]? folders = ListDirectories(folder);
        if (folders is null)
        {
            return;
        }

        foreach (string child in folders)
        {
            string relative = Path.Combine(relativeFolder, Path.GetFileName(child));

            // Only the folders that travel are worth descending into, which keeps a mod's own
            // subfolder from being walked at all rather than walked and then discarded.
            if (!NameComparer.Equals(Path.GetFileName(child), DevourmentConfsFolder))
            {
                continue;
            }

            if (CanonicalPath.IsLink(child))
            {
                skipped.Add(relative);
                continue;
            }

            Walk(root, child, relative, files, skipped);
        }
    }

    private static string[]? Split(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string[] segments = relativePath
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 0 || segments.Any(s => s is "." or "..") ? null : segments;
    }

    private static bool DirectoryExistsSafe(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string[]? ListFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[]? ListDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file's length, or -1 when it cannot be read.</summary>
    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
