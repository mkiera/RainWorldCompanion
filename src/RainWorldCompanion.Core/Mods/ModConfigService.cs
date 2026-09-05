// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;

namespace RainWorldCompanion.Core.Mods;

// Mod settings in the save folder on their own: exported to share, imported from a friend, or
// deleted. An import and a delete change the player's own folder, so both climb the same safety
// snapshot and lock a library load does before it writes there.
public sealed class ModConfigService
{
    private readonly BackupService _backups;
    private readonly string _appVersion;

    public ModConfigService(BackupService backups, string appVersion)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _appVersion = appVersion ?? "";
    }

    public string SaveRoot => _backups.SaveRoot;

    public IReadOnlyList<ModConfigEntry> FilesOf(IReadOnlyCollection<string> modIds)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        var wanted = new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase);
        return ModConfigReader.Read(SaveRoot).Files
            .Where(file => wanted.Contains(file.ModId))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int Export(string destinationPath, IReadOnlyCollection<string> modIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        IReadOnlyList<ModConfigEntry> files = FilesOf(modIds);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "None of the chosen mods has settings in the save folder, so there is nothing to export.");
        }

        var configs = new ModConfigSet { ReadTheFolder = true };
        foreach (ModConfigEntry file in files)
        {
            configs.Files.Add(new ModConfigFile
            {
                RelativePath = file.RelativePath,
                ModId = file.ModId,
                SizeBytes = file.Length,
                Sha256 = Hashing.ComputeFileSha256(file.FullPath),
            });
        }

        var manifest = new ModConfigArchiveManifest
        {
            AppVersion = _appVersion,
            CreatedUtc = DateTime.UtcNow,
            Mods = ModsNamed(modIds),
            Configs = configs,
        };

        ModConfigArchive.Write(
            destinationPath,
            manifest,
            Path.Combine(SaveRoot, ModConfigReader.ModConfigsFolderName));
        return files.Count;
    }

    // Unpacks the file into a holding folder and answers with what it offers. Nothing in the save
    // folder changes until the import's Apply is called, and Dispose clears the holding folder.
    public ModConfigImport BeginImport(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string holding = Path.Combine(
            Path.GetTempPath(),
            "RainWorldCompanion",
            "configs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(holding);

        try
        {
            var warnings = new List<string>();
            ModConfigSet landed = ModConfigArchive.Extract(sourcePath, holding, warnings, out ModConfigArchiveManifest manifest);

            var offer = new ModConfigOffer(landed, manifest.Mods, LiveWithHashes(), TryReadCurrent())
            {
                MachineSpecific = MachineSpecificIn(holding, landed),
            };

            return new ModConfigImport(_backups.SlotCopies, Path.GetFileName(sourcePath), holding, offer, warnings);
        }
        catch
        {
            ModConfigImport.TryDelete(holding);
            throw;
        }
    }

    public SettingsDeleteResult Delete(
        IReadOnlyCollection<string> modIds,
        ModListSnapshot? modsBefore = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        List<string> paths = FilesOf(modIds).Select(file => file.RelativePath).ToList();
        string what = ModConfigWords.Mods(modIds);

        return _backups.SlotCopies.DeleteSettings(
            paths,
            "Before deleting the settings of " + what,
            count => $"Taken automatically before {ModConfigWords.Files(count)} of {what} "
                + (count == 1 ? "was" : "were") + " deleted from the save folder.",
            modsBefore,
            progress,
            ct);
    }

    // Hashed, unlike the panel's own read, so the picker can say Same or Different for each row.
    private ModConfigSet? LiveWithHashes()
    {
        try
        {
            ModConfigScan scan = ModConfigReader.Read(SaveRoot);
            return new ModConfigSet
            {
                ReadTheFolder = scan.ReadTheFolder,
                Note = scan.Note,
                Files = scan.Files
                    .Select(file => new ModConfigFile
                    {
                        RelativePath = file.RelativePath,
                        ModId = file.ModId,
                        SizeBytes = file.Length,
                        Sha256 = Hashing.ComputeFileSha256(file.FullPath),
                    })
                    .ToList(),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private CurrentMods? TryReadCurrent()
    {
        try
        {
            return _backups.ModListSource?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The chosen mods as this machine has them, on or merely installed, so the receiver's picker
    // can name a version. The rest of the list is nobody else's business.
    private ModListSnapshot? ModsNamed(IReadOnlyCollection<string> modIds)
    {
        if (TryReadCurrent() is not { } current)
        {
            return null;
        }

        var wanted = new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase);
        var mods = current.Enabled.Mods.Where(mod => wanted.Contains(mod.Id)).ToList();

        foreach (ModEntry installed in current.Installed)
        {
            if (wanted.Contains(installed.Id)
                && !mods.Any(mod => string.Equals(mod.Id, installed.Id, StringComparison.OrdinalIgnoreCase)))
            {
                mods.Add(installed);
            }
        }

        return new ModListSnapshot
        {
            GameVersion = current.Enabled.GameVersion,
            ReadTheEnabledList = current.Enabled.ReadTheEnabledList,
            CheckedTheInstall = current.Enabled.CheckedTheInstall,
            CheckedTheWorkshop = current.Enabled.CheckedTheWorkshop,
            Note = current.Enabled.Note,
            Mods = mods,
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> MachineSpecificIn(string holding, ModConfigSet landed)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (ModConfigFile file in landed.Files)
        {
            IReadOnlyList<string> keys = ModConfigNotes.MachineSpecificKeys(ModConfigImport.HeldPath(holding, file.RelativePath));
            if (keys.Count > 0)
            {
                found[file.RelativePath] = keys;
            }
        }

        return found;
    }
}

public sealed class ModConfigImport : IDisposable
{
    private readonly SlotCopyService _copies;
    private readonly string _holding;

    internal ModConfigImport(
        SlotCopyService copies,
        string sourceName,
        string holding,
        ModConfigOffer offer,
        IReadOnlyList<string> warnings)
    {
        _copies = copies;
        _holding = holding;
        SourceName = sourceName;
        Offer = offer;
        Warnings = warnings;
    }

    public string SourceName { get; }

    public ModConfigOffer Offer { get; }

    // What the file named but could not deliver whole, one line each.
    public IReadOnlyList<string> Warnings { get; }

    public SettingsWriteResult Apply(
        IReadOnlyCollection<string> modIds,
        ModListSnapshot? modsBefore = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modIds);

        var wanted = new HashSet<string>(modIds, StringComparer.OrdinalIgnoreCase);
        List<ExtraFileWrite> writes = Offer.Recorded.Files
            .Where(file => wanted.Contains(file.ModId))
            .Select(file => new ExtraFileWrite(
                file.RelativePath,
                HeldPath(_holding, file.RelativePath),
                file.Sha256,
                file.RelativePath))
            .ToList();

        return _copies.WriteSettings(
            writes,
            $"Before taking settings from \"{SourceName}\"",
            count => $"Taken automatically before {ModConfigWords.Files(count)} from \"{SourceName}\" "
                + "replaced the ones in the save folder.",
            modsBefore,
            progress,
            ct);
    }

    public void Dispose() => TryDelete(_holding);

    internal static string HeldPath(string holding, string relativePath) =>
        Path.Combine(holding, LibraryEntry.ConfigsFolderName, ModConfigReader.PathBelowFolder(relativePath) ?? "");

    internal static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}

internal static class ModConfigWords
{
    public static string Files(int count) => count == 1 ? "1 file" : count + " files";

    public static string Mods(IReadOnlyCollection<string> ids)
    {
        string[] names = ids.ToArray();
        return names.Length switch
        {
            0 => "no mod",
            1 => names[0],
            2 => names[0] + " and " + names[1],
            _ => string.Join(", ", names.Take(names.Length - 1)) + " and " + names[^1],
        };
    }
}
