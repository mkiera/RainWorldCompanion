// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.IO.Compression;
using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Library;

internal enum BundleKind
{
    Unknown,

    /// <summary>A .rwsave or .rwcampaign zip written by <see cref="SaveBundle.Write"/>.</summary>
    Bundle,

    /// <summary>A bare save container, straight out of a save folder.</summary>
    BareContainer,

    /// <summary>A bare campaign.bin, taken out of a bundle or copied from a library folder.</summary>
    BareCampaign,
}

/// <summary>
/// The .rwsave and .rwcampaign files: a zip holding exactly one stored thing, the mod settings that
/// were beside it, and the manifest describing both. The two extensions are the same format, told
/// apart by whether the archive holds campaign.bin or save.bin rather than by the name or the
/// manifest.
///
/// <para>A destination is never built from a name in the archive. The manifest names each settings
/// file, that recorded path is checked and turned into a destination, and the archive is then asked
/// for that one name. The entry list is never walked, so a name inside a bundle cannot decide where
/// anything lands.</para>
/// </summary>
internal static class SaveBundle
{
    internal const string Extension = ".rwsave";

    internal const string CampaignExtension = ".rwcampaign";

    /// <summary>Where the mod settings sit in the archive, laid out below it the way they sit below
    /// ModConfigs.</summary>
    internal const string ConfigsEntryPrefix = "configs/";

    /// <summary>A cap so a hostile archive declaring a huge entry cannot fill the disk before the
    /// hash check runs.</summary>
    private const long MaxSaveBytes = 256L * 1024 * 1024;

    /// <summary>Devourment's settings file is the largest anyone has, at 80 KB.</summary>
    private const long MaxConfigBytes = 8L * 1024 * 1024;

    private const long MaxConfigsTotalBytes = 64L * 1024 * 1024;

    private const int MaxConfigFiles = 512;

    private const int CopyBufferBytes = 1024 * 128;

    /// <summary>Writes through a temp file, so an interrupted export cannot leave a half written
    /// .rwsave where a whole one used to be.</summary>
    /// <param name="contentFileName">save.bin or campaign.bin, which is what tells a reader which of
    /// the two this is.</param>
    /// <param name="configsDirectory">The entry's configs folder. Null carries no mod settings.</param>
    internal static void Write(
        string destinationPath,
        LibraryManifest manifest,
        string contentPath,
        string contentFileName,
        string? configsDirectory = null)
    {
        var temp = destinationPath + ".tmp";

        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry(LibraryEntry.ManifestFileName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonSerializer.Serialize(manifest, BackupJson.Options));
                }

                var saveEntry = archive.CreateEntry(contentFileName, CompressionLevel.Optimal);
                using (var saveStream = saveEntry.Open())
                using (var source = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    source.CopyTo(saveStream);
                }

                WriteConfigs(archive, manifest, configsDirectory);
            }

            File.Move(temp, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// A settings file that cannot be opened is left out rather than costing the export. The
    /// manifest still names it, and an import warns about what it cannot find, so one mechanism
    /// covers a file missing here and a file missing on the way in.
    /// </summary>
    private static void WriteConfigs(ZipArchive archive, LibraryManifest manifest, string? configsDirectory)
    {
        if (manifest.Configs is not { } configs || string.IsNullOrWhiteSpace(configsDirectory))
        {
            return;
        }

        foreach (var file in configs.Files)
        {
            var below = ConfigSegments(file.RelativePath);
            if (below is null)
            {
                continue;
            }

            FileStream source;
            try
            {
                source = new FileStream(
                    Path.Combine(configsDirectory, Path.Combine(below)),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (source)
            {
                var entry = archive.CreateEntry(EntryNameFor(below), CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                source.CopyTo(entryStream);
            }
        }
    }

    private static string EntryNameFor(string[] segmentsBelowModConfigs)
        => ConfigsEntryPrefix + string.Join('/', segmentsBelowModConfigs);

    /// <summary>
    /// The part of a recorded path below ModConfigs, which is how it is laid out both in an entry
    /// and in an archive. Null for a path that is not one this build carries.
    /// </summary>
    private static string[]? ConfigSegments(string? relativePath)
        => ModConfigReader.Travels(relativePath)
            ? relativePath!
                .Replace('/', '\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .ToArray()
            : null;

    /// <summary>A container always starts with the UTF-8 byte order mark and a zip with the local
    /// file header signature, so the two are told apart without trusting the extension.</summary>
    internal static BundleKind Sniff(string path)
    {
        Span<byte> head = stackalloc byte[32];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var seen = head[..read];

            if (read >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
            {
                return BundleKind.Bundle;
            }

            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                return BundleKind.BareContainer;
            }

            // A campaign file has no mark on the front, so what identifies one is its first record,
            // which is always the campaign.
            if (CampaignFile.LooksLikeOne(seen))
            {
                return BundleKind.BareCampaign;
            }
        }
        catch (Exception)
        {
            return BundleKind.Unknown;
        }

        return BundleKind.Unknown;
    }

    /// <summary>Throws on anything that does not add up, including a save whose bytes do not match
    /// the hash its own manifest recorded: unlike a bare file, there is a recorded answer to check a
    /// bundle against, so one truncated in transit is refused rather than imported with a warning.</summary>
    internal static LibraryManifest Extract(string sourcePath, string destinationDirectory, IList<string> warnings)
    {
        using var archive = ZipFile.OpenRead(sourcePath);

        var manifestEntry = archive.GetEntry(LibraryEntry.ManifestFileName)
            ?? throw new InvalidDataException(
                $"This file has no {LibraryEntry.ManifestFileName} in it, so it is not one this app wrote.");

        // What is in the archive decides which of the two this is, not the manifest and not the name.
        var isCampaign = archive.GetEntry(LibraryEntry.SaveFileName) is null;
        var contentFileName = isCampaign ? LibraryEntry.CampaignFileName : LibraryEntry.SaveFileName;

        var contentEntry = archive.GetEntry(contentFileName)
            ?? throw new InvalidDataException(
                $"This file holds neither {LibraryEntry.SaveFileName} nor {LibraryEntry.CampaignFileName}, so there is nothing in it to import.");

        var cap = isCampaign ? CampaignFile.MaxBytes : MaxSaveBytes;

        if (contentEntry.Length > cap)
        {
            throw new InvalidDataException(
                $"The {contentFileName} in this file says it is {SlotCopyService.FormatSize(contentEntry.Length)}, which is far larger than a Rain World save.");
        }

        LibraryManifest? manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = JsonSerializer.Deserialize<LibraryManifest>(reader.ReadToEnd(), BackupJson.Options);
        }

        if (manifest is null)
        {
            throw new InvalidDataException($"The {LibraryEntry.ManifestFileName} in this file is empty.");
        }

        manifest.Kind = isCampaign ? LibraryEntryKind.Campaign : LibraryEntryKind.WholeSlot;

        var contentPath = Path.Combine(destinationDirectory, contentFileName);

        using (var entryStream = contentEntry.Open())
        using (var destination = new FileStream(contentPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            CopyBounded(entryStream, destination, cap, "a Rain World save");
        }

        var actual = Hashing.ComputeFileSha256(contentPath);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {contentFileName} in this file does not match the checksum recorded beside it, so the file has been " +
                "damaged or altered since it was exported. Nothing was imported.");
        }

        if (manifest.Configs is { Files.Count: > 0 } configs)
        {
            var landed = ExtractConfigs(archive, configs, destinationDirectory, warnings);

            // Nothing landing where settings were named is not the same answer as a save that had
            // none, and the two must never be shown the same way.
            manifest.Configs = landed.Count == 0 ? null : new ModConfigSet
            {
                ReadTheFolder = configs.ReadTheFolder,
                Note = configs.Note,
                Files = landed,
            };
        }

        manifest.SizeBytes = new FileInfo(contentPath).Length;
        return manifest;
    }

    /// <summary>
    /// Writes the mod settings a bundle carries into the entry, and answers with what landed.
    ///
    /// <para>A recorded path that could name somewhere else on the machine throws and refuses the
    /// import: that is a hostile file rather than a damaged one, and this is a format people email
    /// each other. A settings file that is merely absent, oversized or does not match its checksum
    /// is dropped with a warning, because none of those is a reason to refuse the save it came
    /// with.</para>
    /// </summary>
    private static List<ModConfigFile> ExtractConfigs(
        ZipArchive archive,
        ModConfigSet configs,
        string destinationDirectory,
        IList<string> warnings)
    {
        if (configs.Files.Count > MaxConfigFiles)
        {
            throw new InvalidDataException(
                $"This file says it carries {configs.Files.Count} mod settings files, which is far more than a save has. Nothing was imported.");
        }

        var root = Path.Combine(destinationDirectory, LibraryEntry.ConfigsFolderName);
        var landed = new List<ModConfigFile>();
        long written = 0;

        foreach (var file in configs.Files)
        {
            var destination = ResolveConfigDestination(root, file.RelativePath);
            if (destination is null)
            {
                warnings.Add($"{file.RelativePath} is not a kind of mod settings file this version keeps, so it was not imported.");
                continue;
            }

            var entry = archive.GetEntry(EntryNameFor(ConfigSegments(file.RelativePath)!));

            if (entry is null)
            {
                warnings.Add($"{file.RelativePath} is named in this file but is not in it, so it was not imported.");
                continue;
            }

            if (entry.Length > MaxConfigBytes)
            {
                warnings.Add(
                    $"{file.RelativePath} says it is {SlotCopyService.FormatSize(entry.Length)}, which is far larger than a mod settings file, so it was not imported.");
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // Bounded by what is left of the whole allowance, not just this file's, so a hundred
            // entries each lying about their length still cannot write more than the total.
            using (var entryStream = entry.Open())
            using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                written += CopyBounded(
                    entryStream,
                    target,
                    Math.Min(MaxConfigBytes, MaxConfigsTotalBytes - written),
                    "a mod settings file");
            }

            var actual = Hashing.ComputeFileSha256(destination);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                warnings.Add($"{file.RelativePath} does not match the checksum recorded beside it, so it was not imported.");
                continue;
            }

            landed.Add(new ModConfigFile
            {
                RelativePath = file.RelativePath,
                // Worked out from the path by this build's rule rather than taken from the sender,
                // so a mislabelled file cannot be offered under somebody else's mod.
                ModId = ModConfigReader.ModIdFor(file.RelativePath),
                SizeBytes = new FileInfo(destination).Length,
                Sha256 = actual,
            });
        }

        return landed;
    }

    /// <summary>
    /// Where one recorded settings path lands inside the entry. Throws for a path that could name
    /// anywhere but below the entry's own configs folder. Null for a path that is merely not one
    /// this build carries, which a later build may well write.
    /// </summary>
    private static string? ResolveConfigDestination(string root, string? relativePath)
    {
        var text = (relativePath ?? "").Replace('/', '\\');
        var segments = text.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // Segments are deliberately not trimmed: Windows drops trailing spaces and dots while it
        // resolves a path, so a trimmed one would let a recorded path name a file another one holds.
        if (text.Length == 0
            || Path.IsPathRooted(text)
            || segments.Length < 2
            || segments.Any(static segment => segment is "." or "..")
            || !string.Equals(segments[0], ModConfigReader.ModConfigsFolderName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"This file records a mod settings path that does not sit under {ModConfigReader.ModConfigsFolderName}: \"{relativePath}\". Nothing was imported.");
        }

        var below = string.Join('\\', segments.Skip(1));

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, below));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"This file records a mod settings path that is not usable: \"{relativePath}\". Nothing was imported.");
        }

        // The round trip is what catches the trailing dot: a resolved path that does not lead back
        // to the path it was built from does not name the file it claims to.
        if (!CanonicalPath.IsInside(root, candidate)
            || !string.Equals(Path.GetRelativePath(root, candidate), below, StringComparison.OrdinalIgnoreCase)
            || CanonicalPath.LeadsThroughLink(root, candidate))
        {
            throw new InvalidDataException(
                $"This file records a mod settings path that would be written somewhere else: \"{relativePath}\". Nothing was imported.");
        }

        return ModConfigReader.Travels(relativePath) ? candidate : null;
    }

    /// <summary>The declared length in an archive is a claim by whoever wrote it, so the bytes
    /// actually arriving are counted too.</summary>
    private static long CopyBounded(Stream source, Stream destination, long limit, string what)
    {
        var buffer = new byte[CopyBufferBytes];
        long total = 0;

        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return total;
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidDataException(
                    $"What is in this file is larger than {SlotCopyService.FormatSize(limit)}, which is far larger than {what}.");
            }

            destination.Write(buffer, 0, read);
        }
    }
}
