// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.IO.Compression;
using System.Text.Json;
using RainWorldCompanion.Core.Backups;

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
/// The .rwsave and .rwcampaign files: a zip holding exactly one stored thing and the manifest that
/// describes it. The two extensions are the same format, told apart by whether the archive holds
/// campaign.bin or save.bin rather than by the name or the manifest. Only three fixed names are ever
/// read out of one, so no path from inside the archive builds a destination.
/// </summary>
internal static class SaveBundle
{
    internal const string Extension = ".rwsave";

    internal const string CampaignExtension = ".rwcampaign";

    /// <summary>A cap so a hostile archive declaring a huge entry cannot fill the disk before the
    /// hash check runs.</summary>
    private const long MaxSaveBytes = 256L * 1024 * 1024;

    private const int CopyBufferBytes = 1024 * 128;

    /// <summary>Writes through a temp file, so an interrupted export cannot leave a half written
    /// .rwsave where a whole one used to be.</summary>
    /// <param name="contentFileName">save.bin or campaign.bin, which is what tells a reader which of
    /// the two this is.</param>
    internal static void Write(string destinationPath, LibraryManifest manifest, string contentPath, string contentFileName)
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
                using var saveStream = saveEntry.Open();
                using var source = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                source.CopyTo(saveStream);
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
    internal static LibraryManifest Extract(string sourcePath, string destinationDirectory)
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
            CopyBounded(entryStream, destination, cap);
        }

        var actual = Hashing.ComputeFileSha256(contentPath);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {contentFileName} in this file does not match the checksum recorded beside it, so the file has been " +
                "damaged or altered since it was exported. Nothing was imported.");
        }

        manifest.SizeBytes = new FileInfo(contentPath).Length;
        return manifest;
    }

    /// <summary>The declared length in an archive is a claim by whoever wrote it, so the bytes
    /// actually arriving are counted too.</summary>
    private static void CopyBounded(Stream source, Stream destination, long limit)
    {
        var buffer = new byte[CopyBufferBytes];
        long total = 0;

        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return;
            }

            total += read;
            if (total > limit)
            {
                throw new InvalidDataException(
                    $"What is in this file is larger than {SlotCopyService.FormatSize(limit)}, which is far larger than a Rain World save.");
            }

            destination.Write(buffer, 0, read);
        }
    }
}
