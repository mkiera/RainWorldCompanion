// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;

namespace RainWorldCompanion.Core.Mods;

public sealed class ModConfigArchiveManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? AppVersion { get; set; }

    public DateTime CreatedUtc { get; set; }

    // The mods the settings belong to, as the sender's game had them. Null when they were not read.
    public ModListSnapshot? Mods { get; set; }

    public ModConfigSet Configs { get; set; } = new();
}

// The .rwconfigs file: mod settings on their own, laid out below configs/ the way they sit below
// ModConfigs, with a manifest naming each one and its checksum. Read the way a .rwsave is read:
// where a file lands comes from the manifest's recorded path, never from a name in the archive.
public static class ModConfigArchive
{
    public const string Extension = ".rwconfigs";

    public const string ManifestFileName = "configs.json";

    private const long MaxManifestBytes = 4L * 1024 * 1024;

    public static void Write(string destinationPath, ModConfigArchiveManifest manifest, string configsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(configsDirectory);

        string temp = destinationPath + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write(JsonSerializer.Serialize(manifest, BackupJson.Options));
                }

                SaveBundle.WriteConfigs(archive, manifest.Configs, configsDirectory);
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

    public static ModConfigArchiveManifest ReadManifest(string sourcePath)
    {
        using ZipArchive archive = Open(sourcePath);
        return ReadManifest(archive);
    }

    // Lands every settings file the manifest names below destinationDirectory\configs and answers
    // with the ones that arrived whole. A file that is missing, oversized or does not match its
    // checksum is left out with a warning. A path that could land anywhere else refuses the file.
    public static ModConfigSet Extract(
        string sourcePath,
        string destinationDirectory,
        IList<string> warnings,
        out ModConfigArchiveManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(warnings);

        using ZipArchive archive = Open(sourcePath);
        manifest = ReadManifest(archive);

        List<ModConfigFile> landed = SaveBundle.ExtractConfigs(archive, manifest.Configs, destinationDirectory, warnings);
        return new ModConfigSet { ReadTheFolder = true, Files = landed };
    }

    private static ZipArchive Open(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        try
        {
            return ZipFile.OpenRead(sourcePath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("This is not a mod settings file: it is not an archive.");
        }
    }

    private static ModConfigArchiveManifest ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry(ManifestFileName)
            ?? throw new InvalidDataException("This is not a mod settings file: there is no " + ManifestFileName + " in it.");

        if (entry.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("The manifest in this file is far larger than one this app writes.");
        }

        string json;
        using (Stream stream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            SaveBundle.CopyBounded(stream, buffer, MaxManifestBytes, "a manifest");
            json = Encoding.UTF8.GetString(buffer.ToArray());
        }

        ModConfigArchiveManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModConfigArchiveManifest>(json, BackupJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The manifest in this file could not be read: " + ex.Message);
        }

        if (manifest is null)
        {
            throw new InvalidDataException("The manifest in this file is empty.");
        }

        if (manifest.SchemaVersion > ModConfigArchiveManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException("This file was written by a newer version of the app. Update to import it.");
        }

        manifest.Configs ??= new ModConfigSet();
        return manifest;
    }
}
