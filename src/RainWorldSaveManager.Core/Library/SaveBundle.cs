// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.IO.Compression;
using System.Text.Json;
using RainWorldSaveManager.Core.Backups;

namespace RainWorldSaveManager.Core.Library;

/// <summary>What a file offered for import turned out to be.</summary>
internal enum BundleKind
{
    /// <summary>Neither a bundle nor a save container.</summary>
    Unknown,

    /// <summary>A .rwsave zip written by <see cref="SaveBundle.Write"/>.</summary>
    Bundle,

    /// <summary>A bare save container, straight out of a save folder.</summary>
    BareContainer,
}

/// <summary>
/// The .rwsave file: a zip holding exactly the save and the manifest that describes it.
///
/// One file rather than a folder because the point of an export is to be sent somewhere, and the
/// manifest is what carries the name, the note and the campaigns across. A bare save file survives
/// the trip too and can be imported, but it arrives with nothing but its own bytes.
///
/// Only two fixed names are ever read out of a bundle, so no path from inside the archive is ever
/// used to build a destination and a bundle cannot write outside the folder it is extracted into.
/// </summary>
internal static class SaveBundle
{
    /// <summary>The extension an export defaults to.</summary>
    internal const string Extension = ".rwsave";

    /// <summary>
    /// The largest save this will extract. A real container is a few megabytes; the cap is here so
    /// a hostile archive declaring a huge entry cannot fill the disk before the hash check runs.
    /// </summary>
    private const long MaxSaveBytes = 256L * 1024 * 1024;

    private const int CopyBufferBytes = 1024 * 128;

    /// <summary>
    /// Writes a bundle through a temp file, so an interrupted export cannot leave a half written
    /// .rwsave behind where a whole one used to be.
    /// </summary>
    internal static void Write(string destinationPath, LibraryManifest manifest, string savePath)
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

                var saveEntry = archive.CreateEntry(LibraryEntry.SaveFileName, CompressionLevel.Optimal);
                using var saveStream = saveEntry.Open();
                using var source = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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
                    // The export either succeeded and moved the temp away, or failed and reported
                    // why. A leftover temp is not worth a second error on top of the first.
                }
            }
        }
    }

    /// <summary>
    /// Reads the first bytes to decide what a file is. A container always starts with the UTF-8
    /// byte order mark, and a zip always starts with the local file header signature, so the two
    /// are told apart without trusting the extension.
    /// </summary>
    internal static BundleKind Sniff(string path)
    {
        Span<byte> head = stackalloc byte[4];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            if (read >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
            {
                return BundleKind.Bundle;
            }

            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                return BundleKind.BareContainer;
            }
        }
        catch (Exception)
        {
            return BundleKind.Unknown;
        }

        return BundleKind.Unknown;
    }

    /// <summary>
    /// Extracts a bundle's save into <paramref name="destinationDirectory"/> under the standard
    /// name and returns the manifest that came with it.
    ///
    /// Throws on anything that does not add up, including a save whose bytes do not match the hash
    /// its own manifest recorded. That mismatch is what the hash is for: a bundle that was truncated
    /// in transit or edited on the way is refused rather than imported with a warning, because
    /// unlike a bare file there is a recorded answer to check it against.
    /// </summary>
    internal static LibraryManifest Extract(string sourcePath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(sourcePath);

        var manifestEntry = archive.GetEntry(LibraryEntry.ManifestFileName)
            ?? throw new InvalidDataException(
                $"This .rwsave file has no {LibraryEntry.ManifestFileName} in it, so it is not one this app wrote.");

        var saveEntry = archive.GetEntry(LibraryEntry.SaveFileName)
            ?? throw new InvalidDataException(
                $"This .rwsave file has no {LibraryEntry.SaveFileName} in it, so there is no save to import.");

        if (saveEntry.Length > MaxSaveBytes)
        {
            throw new InvalidDataException(
                $"The save in this .rwsave file says it is {SlotCopyService.FormatSize(saveEntry.Length)}, which is far larger than a Rain World save.");
        }

        LibraryManifest? manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = JsonSerializer.Deserialize<LibraryManifest>(reader.ReadToEnd(), BackupJson.Options);
        }

        if (manifest is null)
        {
            throw new InvalidDataException($"The {LibraryEntry.ManifestFileName} in this .rwsave file is empty.");
        }

        var savePath = Path.Combine(destinationDirectory, LibraryEntry.SaveFileName);

        using (var entryStream = saveEntry.Open())
        using (var destination = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            CopyBounded(entryStream, destination, MaxSaveBytes);
        }

        var actual = Hashing.ComputeFileSha256(savePath);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The save in this .rwsave file does not match the checksum recorded beside it, so the file has been " +
                "damaged or altered since it was exported. Nothing was imported.");
        }

        manifest.SizeBytes = new FileInfo(savePath).Length;
        return manifest;
    }

    /// <summary>
    /// Copies until the limit is passed, then throws. The declared length in an archive is a claim
    /// by whoever wrote it, so the bytes actually arriving are counted too.
    /// </summary>
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
                    $"The save in this .rwsave file is larger than {SlotCopyService.FormatSize(limit)}, which is far larger than a Rain World save.");
            }

            destination.Write(buffer, 0, read);
        }
    }
}
