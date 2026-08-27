// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Library;

/// <summary>
/// A folder of named saves. Only <see cref="LoadEntry"/> writes into the live save folder, and it
/// does so through the same steps a slot copy runs: safety snapshot first, operation lock, re-checks,
/// then one copy proved against the hash recorded when the entry was stored. Storing, renaming,
/// deleting, importing and exporting never touch the save folder at all.
/// </summary>
public sealed class SaveLibrary
{
    private static readonly StringComparer HashComparer = StringComparer.OrdinalIgnoreCase;

    private readonly BackupService _backups;
    private readonly IGameProcessDetector _gameDetector;
    private readonly string _appVersion;

    public SaveLibrary(BackupService backups, string libraryRoot, IGameProcessDetector gameDetector, string appVersion)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _gameDetector = gameDetector ?? throw new ArgumentNullException(nameof(gameDetector));
        _appVersion = appVersion ?? "";

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            throw new ArgumentException("Library root must not be empty.", nameof(libraryRoot));
        }

        LibraryRoot = Path.GetFullPath(libraryRoot.Trim());

        var problem = SettingsValidation.Validate(_backups.SaveRoot, _backups.BackupRoot, LibraryRoot);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(libraryRoot));
        }
    }

    public string LibraryRoot { get; }

    public string SaveRoot => _backups.SaveRoot;

    /// <summary>Newest first. A folder that cannot be read is listed with a Problem rather than
    /// dropped.</summary>
    public IReadOnlyList<LibraryEntry> ListEntries()
    {
        var entries = new List<LibraryEntry>();

        try
        {
            if (!Directory.Exists(LibraryRoot))
            {
                return entries;
            }

            foreach (var directory in Directory.EnumerateDirectories(LibraryRoot))
            {
                entries.Add(LibraryEntry.Load(directory));
            }
        }
        catch (Exception)
        {
            return entries;
        }

        // By content time, so a save just updated with an hour of play moves to the top.
        entries.Sort(static (a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
        return entries;
    }

    /// <summary>The entry's own bytes are checked here rather than only at the moment of the write,
    /// so a damaged save is reported in the dialog rather than after the user has agreed.</summary>
    public LibraryLoadPlan PlanLoad(LibraryEntry entry, SaveSlotRef target)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);

        var problems = new List<string>();
        var warnings = new List<string>();

        var side = _backups.SlotCopies.ReadSide(target);

        if (!target.IsRealSlot)
        {
            problems.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Slot {0} is not a Rain World slot. The game has slots {1} to {2}.",
                target.Slot,
                SaveSlotRef.MinSlot,
                SaveSlotRef.MaxSlot));

            return new LibraryLoadPlan(entry, side, problems, warnings);
        }

        if (_gameDetector.IsGameRunning(out var processName))
        {
            problems.Add($"Rain World is running (process \"{processName ?? "Rain World"}\"). Close the game before loading a save.");
        }

        if (entry.Manifest is not { } manifest)
        {
            problems.Add($"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be loaded.");
            return new LibraryLoadPlan(entry, side, problems, warnings);
        }

        // A campaign is spliced into whatever the slot already holds rather than written over it,
        // which is a different operation with a different plan behind it.
        if (entry.IsCampaign)
        {
            problems.Add(
                $"\"{entry.Name}\" holds one campaign rather than a whole slot, so it is loaded into a slot rather than over one.");
            return new LibraryLoadPlan(entry, side, problems, warnings);
        }

        var verification = VerifyEntry(entry);
        if (!verification.Ok)
        {
            problems.Add($"\"{entry.Name}\" failed its checksum check, so it will not be written to a slot.");
            problems.AddRange(verification.Problems);
        }

        // The target has to be in the backup scope: the safety snapshot taken before the load is
        // what makes overwriting it undoable.
        if (!_backups.Scope.IsInScope(side.FileName))
        {
            problems.Add($"{side.FileName} is not one of the files this app manages, so it will not be written to.");
        }
        else if (CanonicalPath.LeadsThroughLink(SaveRoot, side.FullPath))
        {
            problems.Add($"{side.FileName} is a link, so writing to it would land outside the save folder.");
        }

        if (manifest.Metadata?.ParseError is { } parseError)
        {
            warnings.Add($"\"{entry.Name}\" cannot be read by this app ({parseError}). It will still be loaded exactly as it is.");
        }

        if (manifest.Metadata?.ChecksumValid == false)
        {
            warnings.Add($"\"{entry.Name}\" has a checksum the game will reject, and loading it does not repair that.");
        }

        if (side.Exists && side.Metadata?.ParseError is { } targetError)
        {
            warnings.Add($"{side.FileName} cannot be read by this app ({targetError}), so what is about to be replaced cannot be described.");
        }

        var entryCampaigns = manifest.Metadata?.Campaigns.Count ?? 0;
        var targetCampaigns = side.Metadata?.Campaigns.Count ?? 0;

        if (entryCampaigns == 0 && targetCampaigns > 0)
        {
            // A save with records but no SAVE STATE is not empty: it is a Rain Meadow online save
            // holding the explored map and the progression record.
            warnings.Add(manifest.Metadata?.RecordCount > 0
                ? $"\"{entry.Name}\" holds no campaign, only map and progression data, so this leaves {side.FileName} with no campaign in it."
                : $"\"{entry.Name}\" holds no campaign, so this replaces {side.FileName} with an empty slot.");
        }

        WarnAboutSettings(manifest, warnings);

        return new LibraryLoadPlan(entry, side, problems, warnings);
    }

    /// <summary>
    /// Names any recorded settings file that could not be written where it says it goes, so it is
    /// said in the dialog rather than after the user has agreed. Never a problem: a settings file
    /// that will not land is no reason to refuse the save.
    /// </summary>
    private void WarnAboutSettings(LibraryManifest manifest, List<string> warnings)
    {
        if (manifest.Configs is not { } configs)
        {
            return;
        }

        foreach (var file in configs.Files)
        {
            var relative = file.RelativePath;

            if (!_backups.Scope.IsInScope(relative))
            {
                warnings.Add($"{relative} is not one of the files this app manages, so those settings will not be written.");
            }
            else if (!ModConfigReader.Travels(relative))
            {
                warnings.Add($"{relative} is not a kind of mod settings file this version writes, so it will not be.");
            }
            else if (CanonicalPath.LeadsThroughLink(SaveRoot, Path.Combine(SaveRoot, relative)))
            {
                warnings.Add($"{relative} is a link in the save folder, so those settings will not be written over it.");
            }
        }
    }

    /// <summary>The entry's recorded digest goes down with it, so a save damaged since it was stored
    /// is refused under the lock rather than written.</summary>
    public LibraryLoadResult LoadEntry(
        LibraryEntry entry,
        SaveSlotRef target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => LoadEntry(entry, target, Array.Empty<string>(), progress, ct);

    /// <param name="adoptSettingsFor">The mods whose settings to bring across, by id. Empty takes
    /// none, which is what a dialog opens on: somebody else's settings are not what a player asked
    /// for by asking to load a save.</param>
    public LibraryLoadResult LoadEntry(
        LibraryEntry entry,
        SaveSlotRef target,
        IReadOnlyCollection<string> adoptSettingsFor,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(adoptSettingsFor);

        var errors = new List<string>();

        EnsureGameNotRunning();

        var plan = PlanLoad(entry, target);
        var warnings = new List<string>(plan.Warnings);

        if (!plan.CanLoad)
        {
            errors.AddRange(plan.Problems);
            return new LibraryLoadResult(false, null, errors, warnings, false, 0, plan);
        }

        ct.ThrowIfCancellationRequested();

        var manifest = entry.Manifest!;
        var quotedName = "\"" + entry.Name + "\"";

        var job = new SlotWriteJob(
            plan.Target,
            entry.SavePath,
            quotedName,
            manifest.Sha256,
            OperationNoun: "load",
            ProgressVerb: "Loading",
            SafetyLabel: $"Before loading {quotedName} into {plan.Target.FileName}",
            SafetyNote: targetExists => targetExists
                ? $"Automatic copy taken before the library save {quotedName} was loaded over {plan.Target.FileName} ({plan.Target.Describe()})."
                : $"Automatic copy taken before the library save {quotedName} was loaded into {plan.Target.FileName}, which did not exist yet.",
            Extras: BuildExtras(entry, adoptSettingsFor));

        var outcome = _backups.SlotCopies.CopyOntoSlot(job, progress, ct);

        errors.AddRange(outcome.Errors);
        warnings.AddRange(outcome.Warnings);

        var success = outcome.Success && errors.Count == 0;

        if (success)
        {
            RecordSlotLink(entry, manifest, plan.Target.FullPath, target);
        }

        return new LibraryLoadResult(
            success,
            outcome.SafetySnapshot,
            errors,
            warnings,
            outcome.LiveFolderModified,
            outcome.BytesCopied,
            plan)
        {
            SettingsWritten = outcome.ExtrasWritten,
        };
    }

    /// <summary>
    /// The settings files to write, for the mods that were asked for. Grouping by mod is what makes
    /// writing a subset fall out: nothing in the writer knows what a mod is.
    /// </summary>
    private static IReadOnlyList<ExtraFileWrite> BuildExtras(
        LibraryEntry entry,
        IReadOnlyCollection<string> adoptSettingsFor)
    {
        if (adoptSettingsFor.Count == 0 || entry.Manifest?.Configs is not { } configs)
        {
            return Array.Empty<ExtraFileWrite>();
        }

        var wanted = new HashSet<string>(adoptSettingsFor, StringComparer.OrdinalIgnoreCase);
        var extras = new List<ExtraFileWrite>();

        foreach (var file in configs.Files)
        {
            if (!wanted.Contains(file.ModId))
            {
                continue;
            }

            var source = ConfigEntryPath(entry.DirectoryPath, file.RelativePath);
            if (source is not null)
            {
                extras.Add(new ExtraFileWrite(file.RelativePath, source, file.Sha256, file.RelativePath));
            }
        }

        return extras;
    }

    /// <summary>One slot, one claimant: the slot is taken away from whichever entry claimed it
    /// before. Best effort, because the bytes are already where they belong by this point and a
    /// manifest that could not be rewritten costs only a hint on a row.</summary>
    private void RecordSlotLink(LibraryEntry entry, LibraryManifest manifest, string slotPath, SaveSlotRef slot)
    {
        try
        {
            var info = new FileInfo(slotPath);

            manifest.LastLoadedRealm = slot.Realm;
            manifest.LastLoadedSlot = slot.Slot;
            manifest.LastLoadedUtc = DateTime.UtcNow;
            manifest.LastLoadedSizeBytes = info.Exists ? info.Length : null;
            manifest.LastLoadedWriteUtc = info.Exists ? info.LastWriteTimeUtc : null;

            WriteManifest(entry.DirectoryPath, manifest);
        }
        catch (Exception)
        {
        }

        ReleaseSlot(slot, exceptEntryId: entry.Id);
    }

    /// <summary>Call this when something other than a library load writes to the slot, such as a
    /// restore or a slot copy.</summary>
    public void ReleaseSlot(SaveSlotRef slot) => ReleaseSlot(slot, exceptEntryId: null);

    /// <summary>Forgets every slot claim, which is what a whole folder restore invalidates.</summary>
    public void ReleaseAllSlots()
    {
        foreach (var entry in ListEntries())
        {
            if (entry.Manifest is { LastLoadedRealm: not null } manifest)
            {
                ClearSlotLink(entry, manifest);
            }
        }
    }

    private void ReleaseSlot(SaveSlotRef slot, string? exceptEntryId)
    {
        ArgumentNullException.ThrowIfNull(slot);

        foreach (var entry in ListEntries())
        {
            if (string.Equals(entry.Id, exceptEntryId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Manifest is { } manifest
                && manifest.LastLoadedRealm == slot.Realm
                && manifest.LastLoadedSlot == slot.Slot)
            {
                ClearSlotLink(entry, manifest);
            }
        }
    }

    private static void ClearSlotLink(LibraryEntry entry, LibraryManifest manifest)
    {
        try
        {
            manifest.LastLoadedRealm = null;
            manifest.LastLoadedSlot = null;
            manifest.LastLoadedUtc = null;
            manifest.LastLoadedSizeBytes = null;
            manifest.LastLoadedWriteUtc = null;

            WriteManifest(entry.DirectoryPath, manifest);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Throws while the game is running, as CreateBackup and CopySlot do. The copy is proved
    /// against its source before the manifest records it.</summary>
    public LibraryEntry StoreSlot(
        SaveSlotRef source,
        string name,
        string? note,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var trimmedName = (name ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A library save needs a name.", nameof(name));
        }

        EnsureGameNotRunning();
        ct.ThrowIfCancellationRequested();

        var sourcePath = ResolveSlotPath(source);

        // Reading the live folder is held against the same lock a restore takes, so a restore in
        // another window cannot rewrite the slot half way through this copy.
        using var lease = _backups.AcquireOperationLock();

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"{source.FileName} is not in the save folder, so there is nothing to store.", sourcePath);
        }

        if (CanonicalPath.IsLink(sourcePath))
        {
            throw new IOException($"{source.FileName} is a link, and this app copies only real files inside the save folder.");
        }

        var directory = TimestampedFolders.Create(LibraryRoot, LibraryEntry.ClaimFileName, "library folder");
        var savePath = Path.Combine(directory, LibraryEntry.SaveFileName);

        var copied = CopyProving(sourcePath, savePath, source.FileName, progress);

        progress?.Report($"Reading what is in {source.FileName}");
        var metadata = SaveMetadataExtractor.Extract(savePath, source.Slot, source.Realm);

        var manifest = new LibraryManifest
        {
            SchemaVersion = LibraryManifest.CurrentSchemaVersion,
            Name = trimmedName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedUtc = DateTime.UtcNow,
            AppVersion = _appVersion,
            SourceFileName = source.FileName,
            SourceRealm = source.Realm,
            SourceSlot = source.Slot,
            SizeBytes = copied.SizeBytes,
            Sha256 = copied.Sha256,
            Metadata = metadata,
            Mods = _backups.TryReadMods(),
            Configs = StoreConfigs(directory, SaveRoot),
        };

        TimestampedFolders.ReleaseClaim(directory, LibraryEntry.ClaimFileName);
        WriteManifest(directory, manifest);

        progress?.Report("Stored");
        return LibraryEntry.Load(directory);
    }

    /// <summary>The slot is left exactly as it was: storing a campaign is a read.</summary>
    public LibraryEntry StoreCampaign(
        SaveSlotRef source,
        string slugcatId,
        string name,
        string? note,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var trimmedName = (name ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A library save needs a name.", nameof(name));
        }

        EnsureGameNotRunning();
        ct.ThrowIfCancellationRequested();

        var sourcePath = ResolveSlotPath(source);

        using var lease = _backups.AcquireOperationLock();

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"{source.FileName} is not in the save folder, so there is nothing to store.", sourcePath);
        }

        if (CanonicalPath.IsLink(sourcePath))
        {
            throw new IOException($"{source.FileName} is a link, and this app copies only real files inside the save folder.");
        }

        progress?.Report($"Reading {source.FileName}");

        var slice = SaveEditSession.Open(sourcePath).TakeCampaign(slugcatId)
            ?? throw new InvalidOperationException(
                $"{source.FileName} holds no {SlugcatCatalog.ForId(slugcatId).DisplayName} campaign, so there is nothing to store.");

        var entry = StoreCampaignFrom(
            slice, source.FileName, source.Realm, source.Slot, trimmedName, note, _backups.TryReadMods(), SaveRoot);

        progress?.Report("Stored");
        return entry;
    }

    /// <summary>Keeps a campaign already in hand. Unlike <see cref="StoreCampaign"/> this takes no
    /// lock and does not care whether the game is running, because it touches no save file.</summary>
    /// <param name="mods">The mods that were on when the campaign was taken. Passed in because a
    /// campaign pulled out of a backup carries that backup's record, not what is on right now.</param>
    /// <param name="configsRoot">The folder holding the ModConfigs that go with those bytes, which
    /// for a campaign out of a backup is that backup's folder. Null keeps no settings.</param>
    public LibraryEntry StoreCampaignFrom(
        CampaignSlice slice,
        string sourceFileName,
        SaveRealm sourceRealm,
        int sourceSlot,
        string name,
        string? note,
        ModListSnapshot? mods = null,
        string? configsRoot = null)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var trimmedName = (name ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A library save needs a name.", nameof(name));
        }

        var payload = CampaignFile.ToPayload(slice);
        var directory = TimestampedFolders.Create(LibraryRoot, LibraryEntry.ClaimFileName, "library folder");
        var campaignPath = Path.Combine(directory, LibraryEntry.CampaignFileName);

        File.WriteAllBytes(campaignPath, CampaignFile.ToBytes(slice));

        var manifest = new LibraryManifest
        {
            SchemaVersion = LibraryManifest.CurrentSchemaVersion,
            Kind = LibraryEntryKind.Campaign,
            CampaignSlugcatId = slice.SlugcatId,
            Name = trimmedName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedUtc = DateTime.UtcNow,
            AppVersion = _appVersion,
            SourceFileName = sourceFileName ?? "",
            SourceRealm = sourceRealm,
            SourceSlot = sourceSlot,
            SizeBytes = new FileInfo(campaignPath).Length,
            Sha256 = Hashing.ComputeFileSha256(campaignPath),
            Metadata = SaveMetadataExtractor.FromPayload(
                payload, LibraryEntry.CampaignFileName, sourceSlot, sourceRealm),
            Mods = mods,
            Configs = StoreConfigs(directory, configsRoot),
        };

        TimestampedFolders.ReleaseClaim(directory, LibraryEntry.ClaimFileName);
        WriteManifest(directory, manifest);

        return LibraryEntry.Load(directory);
    }

    /// <summary>A campaign goes into whatever the slot already holds rather than over it, so the
    /// other campaigns in that slot are untouched.</summary>
    public CampaignMovePlan PlanCampaignLoad(LibraryEntry entry, SaveSlotRef target)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);

        var side = _backups.SlotCopies.ReadSide(target);
        var name = side.FileName.Length == 0 ? "the target slot" : side.FileName;

        if (!entry.IsCampaign)
        {
            return CampaignMovePlan.Refused(
                side.FullPath,
                target,
                name,
                $"\"{entry.Name}\" holds a whole slot rather than one campaign, so it is written over a slot rather than into one.");
        }

        var verification = VerifyEntry(entry);
        if (!verification.Ok)
        {
            return CampaignMovePlan.Refused(
                side.FullPath,
                target,
                name,
                new[] { $"\"{entry.Name}\" failed its checksum check, so it will not be written to a slot." }
                    .Concat(verification.Problems)
                    .ToArray());
        }

        if (ReadStoredCampaign(entry) is not { } slice)
        {
            return CampaignMovePlan.Refused(
                side.FullPath,
                target,
                name,
                $"\"{entry.Name}\" does not read back as a campaign, so it will not be written to a slot.");
        }

        return _backups.SlotWriter.PlanPutCampaign(target, slice);
    }

    /// <summary>What is written is the slot's own bytes with one campaign spliced in, rather than a
    /// file copied over the top.</summary>
    public LibraryLoadResult LoadCampaignOntoSlot(
        LibraryEntry entry,
        SaveSlotRef target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);

        EnsureGameNotRunning();

        var side = _backups.SlotCopies.ReadSide(target);
        var move = PlanCampaignLoad(entry, target);
        var plan = new LibraryLoadPlan(entry, side, move.Problems, move.Warnings, move.Describe());

        if (!move.CanWrite)
        {
            var problems = move.Problems.Count > 0 ? move.Problems : move.Write.Problems;
            return new LibraryLoadResult(false, null, problems, move.Warnings, false, 0, plan);
        }

        ct.ThrowIfCancellationRequested();

        var result = _backups.SlotWriter.Write(move, progress, ct);
        var warnings = move.Warnings.Concat(result.Warnings).ToArray();

        if (result.Success)
        {
            // A slot holding one campaign from this entry and everything else of its own is not the
            // entry's bytes, so no claim on the slot is recorded and any older one still stands.
            progress?.Report(move.Describe());
        }

        return new LibraryLoadResult(
            result.Success,
            result.SafetySnapshot,
            result.Errors,
            warnings,
            result.LiveFolderModified,
            result.BytesWritten,
            plan);
    }

    /// <summary>A whole slot is written over the target and a campaign is written into it, so a
    /// caller offering both goes through here rather than knowing which it holds.</summary>
    public LibraryLoadPlan PlanAnyLoad(LibraryEntry entry, SaveSlotRef target)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);

        // Attached here rather than in each branch: this is the one method the dialogs go through.
        var mods = _backups.TryDiffMods(entry.Manifest?.Mods);
        var settings = BuildOffer(entry);

        if (!entry.IsCampaign)
        {
            return PlanLoad(entry, target) with { Mods = mods, Settings = settings };
        }

        var move = PlanCampaignLoad(entry, target);
        var problems = move.Problems.Count > 0 ? move.Problems : move.Write.Problems;

        return new LibraryLoadPlan(
            entry,
            _backups.SlotCopies.ReadSide(target),
            problems,
            move.Warnings,
            move.Describe())
        {
            Mods = mods,
            Settings = settings,
        };
    }

    /// <summary>What this entry can bring across, or null when it carries no settings.</summary>
    private ModConfigOffer? BuildOffer(LibraryEntry entry)
    {
        if (entry.Manifest?.Configs is not { Files.Count: > 0 } recorded)
        {
            return null;
        }

        ModConfigSet? live = null;
        try
        {
            var scan = ModConfigReader.Read(SaveRoot);
            live = new ModConfigSet
            {
                ReadTheFolder = scan.ReadTheFolder,
                Note = scan.Note,
                Files = scan.Files
                    .Select(file => new ModConfigFile { RelativePath = file.RelativePath, ModId = file.ModId })
                    .ToList(),
            };
        }
        catch (Exception)
        {
        }

        CurrentMods? current = null;
        try
        {
            current = _backups.ModListSource?.Invoke();
        }
        catch (Exception)
        {
        }

        return new ModConfigOffer(recorded, entry.Manifest.Mods, live, current)
        {
            MachineSpecific = ReadMachineSpecific(entry, recorded),
        };
    }

    /// <summary>
    /// Which recorded settings hold a key naming something about the machine. Read from the entry's
    /// own copy rather than the save folder's: what a picker warns about is what would be written.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadMachineSpecific(
        LibraryEntry entry,
        ModConfigSet recorded)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in recorded.Files)
        {
            if (ConfigEntryPath(entry.DirectoryPath, file.RelativePath) is not { } path)
            {
                continue;
            }

            var keys = ModConfigNotes.MachineSpecificKeys(path);
            if (keys.Count > 0)
            {
                found[file.RelativePath] = keys;
            }
        }

        return found;
    }

    public LibraryLoadResult LoadAny(
        LibraryEntry entry,
        SaveSlotRef target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => LoadAny(entry, target, Array.Empty<string>(), progress, ct);

    public LibraryLoadResult LoadAny(
        LibraryEntry entry,
        SaveSlotRef target,
        IReadOnlyCollection<string> adoptSettingsFor,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.IsCampaign
            ? LoadCampaignOntoSlot(entry, target, progress, ct)
            : LoadEntry(entry, target, adoptSettingsFor, progress, ct);
    }

    /// <summary>The campaign an entry holds, or null when it holds a whole slot or will not read.</summary>
    public CampaignSlice? ReadStoredCampaign(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.IsCampaign)
        {
            return null;
        }

        try
        {
            return CampaignFile.Read(File.ReadAllBytes(entry.CampaignPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The bytes being replaced are kept as save.previous.bin so the update can be undone.
    /// One generation only: the next update replaces it.</summary>
    public LibraryEntry UpdateEntry(
        LibraryEntry entry,
        SaveSlotRef source,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);

        EnsureOwnedEntry(entry, "update");

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be updated.");
        }

        EnsureGameNotRunning();
        ct.ThrowIfCancellationRequested();

        var sourcePath = ResolveSlotPath(source);

        using var lease = _backups.AcquireOperationLock();

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"{source.FileName} is not in the save folder, so there is nothing to update from.", sourcePath);
        }

        if (CanonicalPath.IsLink(sourcePath))
        {
            throw new IOException($"{source.FileName} is a link, and this app copies only real files inside the save folder.");
        }

        if (entry.IsCampaign)
        {
            return UpdateCampaign(entry, manifest, source, sourcePath, progress);
        }

        // The new bytes land beside the old ones and are proved before anything is replaced, so a
        // copy that goes wrong leaves the entry exactly as it was.
        var stagedPath = Path.Combine(entry.DirectoryPath, LibraryEntry.SaveFileName + ".tmp");
        var copied = CopyProving(sourcePath, stagedPath, source.FileName, progress);

        progress?.Report($"Reading what is in {source.FileName}");
        var metadata = SaveMetadataExtractor.Extract(stagedPath, source.Slot, source.Realm);

        File.Move(entry.SavePath, entry.PreviousSavePath, overwrite: true);
        File.Move(stagedPath, entry.SavePath, overwrite: true);

        manifest.PreviousSha256 = manifest.Sha256;
        manifest.PreviousSizeBytes = manifest.SizeBytes;
        manifest.PreviousReplacedUtc = DateTime.UtcNow;
        manifest.PreviousMetadata = manifest.Metadata;
        manifest.PreviousMods = manifest.Mods;
        manifest.PreviousConfigs = MoveConfigsAside(entry) ? manifest.Configs : null;

        manifest.SizeBytes = copied.SizeBytes;
        manifest.Sha256 = copied.Sha256;
        manifest.Metadata = metadata;
        manifest.Mods = _backups.TryReadMods();
        manifest.Configs = StoreConfigs(entry.DirectoryPath, SaveRoot);
        manifest.SourceFileName = source.FileName;
        manifest.SourceRealm = source.Realm;
        manifest.SourceSlot = source.Slot;
        manifest.UpdatedUtc = DateTime.UtcNow;

        WriteManifest(entry.DirectoryPath, manifest);

        // The entry now holds exactly what the slot holds, so the link is new again. Without this a
        // row reads "changed since" about the very slot it was just brought level with.
        RecordSlotLink(entry, manifest, sourcePath, source);

        progress?.Report("Updated");
        return LibraryEntry.Load(entry.DirectoryPath);
    }

    /// <summary>No slot link is recorded, because a slot holding this campaign and eight others is
    /// not this entry's bytes.</summary>
    private LibraryEntry UpdateCampaign(
        LibraryEntry entry,
        LibraryManifest manifest,
        SaveSlotRef source,
        string sourcePath,
        IProgress<string>? progress)
    {
        var slugcat = manifest.CampaignSlugcatId ?? "";

        progress?.Report($"Reading {source.FileName}");

        var slice = SaveEditSession.Open(sourcePath).TakeCampaign(slugcat)
            ?? throw new InvalidOperationException(
                $"{source.FileName} holds no {SlugcatCatalog.ForId(slugcat).DisplayName} campaign, so there is nothing to update from.");

        var payload = CampaignFile.ToPayload(slice);
        var stagedPath = Path.Combine(entry.DirectoryPath, LibraryEntry.CampaignFileName + ".tmp");

        File.WriteAllBytes(stagedPath, CampaignFile.ToBytes(slice));

        File.Move(entry.CampaignPath, entry.PreviousContentPath, overwrite: true);
        File.Move(stagedPath, entry.CampaignPath, overwrite: true);

        manifest.PreviousSha256 = manifest.Sha256;
        manifest.PreviousSizeBytes = manifest.SizeBytes;
        manifest.PreviousReplacedUtc = DateTime.UtcNow;
        manifest.PreviousMetadata = manifest.Metadata;
        manifest.PreviousMods = manifest.Mods;
        manifest.PreviousConfigs = MoveConfigsAside(entry) ? manifest.Configs : null;
        manifest.Mods = _backups.TryReadMods();
        manifest.Configs = StoreConfigs(entry.DirectoryPath, SaveRoot);

        manifest.SizeBytes = new FileInfo(entry.CampaignPath).Length;
        manifest.Sha256 = Hashing.ComputeFileSha256(entry.CampaignPath);
        manifest.Metadata = SaveMetadataExtractor.FromPayload(
            payload, LibraryEntry.CampaignFileName, source.Slot, source.Realm);
        manifest.CampaignSlugcatId = slice.SlugcatId;
        manifest.SourceFileName = source.FileName;
        manifest.SourceRealm = source.Realm;
        manifest.SourceSlot = source.Slot;
        manifest.UpdatedUtc = DateTime.UtcNow;

        WriteManifest(entry.DirectoryPath, manifest);

        progress?.Report("Updated");
        return LibraryEntry.Load(entry.DirectoryPath);
    }

    /// <summary>Refuses when the previous bytes are not both on disk and recorded, and proves them
    /// against the recorded hash before swapping them in.</summary>
    public LibraryEntry UndoUpdate(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOwnedEntry(entry, "undo the update of");

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so there is nothing to undo.");
        }

        if (manifest.PreviousSha256 is not { Length: > 0 } previousHash || !File.Exists(entry.PreviousContentPath))
        {
            throw new InvalidOperationException($"\"{entry.Name}\" has no earlier save to go back to.");
        }

        var actual = Hashing.ComputeFileSha256(entry.PreviousContentPath);
        if (!HashComparer.Equals(actual, previousHash))
        {
            throw new IOException(
                $"The earlier save kept for \"{entry.Name}\" does not match its recorded checksum, so it was not put back.");
        }

        var metadata = manifest.PreviousMetadata;

        var stagedPath = Path.Combine(entry.DirectoryPath, entry.ContentFileName + ".tmp");
        File.Copy(entry.PreviousContentPath, stagedPath, overwrite: true);
        File.Move(stagedPath, entry.ContentPath, overwrite: true);

        manifest.SizeBytes = manifest.PreviousSizeBytes ?? new FileInfo(entry.ContentPath).Length;
        manifest.Sha256 = previousHash;
        manifest.Metadata = metadata;

        manifest.Mods = manifest.PreviousMods;

        // A record that outlived the files it describes would be worse than none, so the settings
        // only go back in the manifest if they went back on disk.
        if (MoveConfigsBack(entry))
        {
            manifest.Configs = manifest.PreviousConfigs;
        }

        manifest.PreviousSha256 = null;
        manifest.PreviousSizeBytes = null;
        manifest.PreviousReplacedUtc = null;
        manifest.PreviousMetadata = null;
        manifest.PreviousMods = null;
        manifest.PreviousConfigs = null;

        manifest.UpdatedUtc = DateTime.UtcNow;

        // The entry now holds the older save again, which is not what any slot holds, so it is in
        // no slot until it is loaded somewhere.
        manifest.LastLoadedRealm = null;
        manifest.LastLoadedSlot = null;
        manifest.LastLoadedUtc = null;
        manifest.LastLoadedSizeBytes = null;
        manifest.LastLoadedWriteUtc = null;

        WriteManifest(entry.DirectoryPath, manifest);

        TryDelete(entry.PreviousContentPath);

        return LibraryEntry.Load(entry.DirectoryPath);
    }

    /// <summary>Rewrites entry.json and nothing else: the folder keeps its timestamp name.</summary>
    public LibraryEntry RenameEntry(LibraryEntry entry, string newName, string? newNote)
    {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOwnedEntry(entry, "rename");

        var trimmedName = (newName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A library save needs a name.", nameof(newName));
        }

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be renamed.");
        }

        manifest.Name = trimmedName;
        manifest.Note = string.IsNullOrWhiteSpace(newNote) ? null : newNote.Trim();

        WriteManifest(entry.DirectoryPath, manifest);
        return LibraryEntry.Load(entry.DirectoryPath);
    }

    /// <summary>Deletes an entry folder. Refuses anything that is not a direct child of the root.</summary>
    public void DeleteEntry(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOwnedEntry(entry, "delete");

        if (!Directory.Exists(entry.DirectoryPath))
        {
            return;
        }

        Directory.Delete(entry.DirectoryPath, recursive: true);
    }

    public VerifyResult VerifyEntry(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var problems = new List<string>();

        if (entry.Manifest is not { } manifest)
        {
            problems.Add(entry.Problem ?? "entry.json is missing");
            return new VerifyResult(false, problems);
        }

        var what = entry.IsCampaign ? "the stored campaign" : "the stored save";

        FileInfo info;
        try
        {
            info = new FileInfo(entry.ContentPath);
            if (!info.Exists)
            {
                problems.Add(what + " is missing");
                return new VerifyResult(false, problems);
            }
        }
        catch (Exception ex)
        {
            problems.Add(what + " could not be read: " + ex.Message);
            return new VerifyResult(false, problems);
        }

        if (info.Length != manifest.SizeBytes)
        {
            problems.Add($"{what} is {info.Length} bytes and was recorded as {manifest.SizeBytes}");
        }

        try
        {
            var actual = Hashing.ComputeFileSha256(entry.ContentPath);
            if (!HashComparer.Equals(actual, manifest.Sha256))
            {
                problems.Add(what + " does not match its recorded checksum");
            }
        }
        catch (Exception ex)
        {
            problems.Add(what + " could not be read: " + ex.Message);
        }

        return new VerifyResult(problems.Count == 0, problems);
    }

    public static string ExportExtensionFor(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.IsCampaign ? SaveBundle.CampaignExtension : SaveBundle.Extension;
    }

    /// <summary>A whole slot goes out as .rwsave and one campaign as .rwcampaign.</summary>
    public void ExportEntry(LibraryEntry entry, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("An export needs somewhere to write to.", nameof(destinationPath));
        }

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be exported.");
        }

        var verification = VerifyEntry(entry);
        if (!verification.Ok)
        {
            throw new IOException(
                $"\"{entry.Name}\" failed its checksum check, so it was not exported: " + string.Join("; ", verification.Problems));
        }

        SaveBundle.Write(
            Path.GetFullPath(destinationPath),
            manifest,
            entry.ContentPath,
            entry.ContentFileName,
            entry.ConfigsPath);
    }

    /// <summary>An import never writes into the save folder: it lands in the library and is loaded
    /// from there like anything else.</summary>
    public LibraryImportResult ImportFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new LibraryImportResult(null, Array.Empty<string>(), new[] { "No file was chosen." });
        }

        var full = Path.GetFullPath(sourcePath.Trim());
        var warnings = new List<string>();

        if (!File.Exists(full))
        {
            return new LibraryImportResult(null, warnings, new[] { $"{Path.GetFileName(full)} is not there." });
        }

        var kind = SaveBundle.Sniff(full);
        if (kind == BundleKind.Unknown)
        {
            return new LibraryImportResult(null, warnings, new[]
            {
                $"{Path.GetFileName(full)} is not a Rain World save, a {SaveBundle.Extension} or a {SaveBundle.CampaignExtension} file.",
            });
        }

        var directory = TimestampedFolders.Create(LibraryRoot, LibraryEntry.ClaimFileName, "library folder");

        try
        {
            var manifest = kind switch
            {
                BundleKind.Bundle => ImportBundle(full, directory, warnings),
                BundleKind.BareCampaign => ImportBareCampaign(full, directory, warnings),
                _ => ImportBareContainer(full, directory, warnings),
            };

            manifest.SchemaVersion = LibraryManifest.CurrentSchemaVersion;

            // The load history belonged to whoever exported it and says nothing about this machine.
            manifest.LastLoadedRealm = null;
            manifest.LastLoadedSlot = null;
            manifest.LastLoadedUtc = null;
            manifest.LastLoadedSizeBytes = null;
            manifest.LastLoadedWriteUtc = null;

            // The bundle carries one save, so there is no earlier generation to go back to here.
            manifest.PreviousSha256 = null;
            manifest.PreviousSizeBytes = null;
            manifest.PreviousReplacedUtc = null;
            manifest.PreviousMetadata = null;
            manifest.PreviousMods = null;
            manifest.PreviousConfigs = null;

            // manifest.Mods and manifest.Configs are deliberately left standing: they describe the
            // machine the save was played on, which is what a load onto this machine is compared
            // against and what its settings are offered from.

            TimestampedFolders.ReleaseClaim(directory, LibraryEntry.ClaimFileName);
            WriteManifest(directory, manifest);

            return new LibraryImportResult(LibraryEntry.Load(directory), warnings, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(directory);
            return new LibraryImportResult(null, warnings, new[] { ex.Message });
        }
    }

    private static LibraryManifest ImportBundle(string sourcePath, string directory, List<string> warnings)
    {
        var manifest = SaveBundle.Extract(sourcePath, directory, warnings);

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            manifest.Name = Path.GetFileNameWithoutExtension(sourcePath);
        }

        return manifest;
    }

    /// <summary>A campaign file that arrived on its own has no recorded hash to be held to, so one
    /// that will not read back is imported with a warning rather than refused.</summary>
    private LibraryManifest ImportBareCampaign(string sourcePath, string directory, List<string> warnings)
    {
        var campaignPath = Path.Combine(directory, LibraryEntry.CampaignFileName);
        var fileName = Path.GetFileName(sourcePath);

        var copied = CopyProving(sourcePath, campaignPath, fileName, progress: null);

        var payload = File.ReadAllBytes(campaignPath);
        var slice = CampaignFile.Read(payload);

        if (slice is null)
        {
            warnings.Add($"{fileName} does not read back as a campaign. It was imported exactly as it is.");
        }

        var metadata = SaveMetadataExtractor.FromPayload(
            Encoding.UTF8.GetString(payload), LibraryEntry.CampaignFileName, 0, SaveRealm.Local);

        return new LibraryManifest
        {
            SchemaVersion = LibraryManifest.CurrentSchemaVersion,
            Kind = LibraryEntryKind.Campaign,
            CampaignSlugcatId = slice?.SlugcatId,
            Name = Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem ? stem : fileName,
            Note = null,
            CreatedUtc = DateTime.UtcNow,
            AppVersion = _appVersion,
            SourceFileName = fileName,
            SourceRealm = SaveRealm.Local,
            SourceSlot = 0,
            SizeBytes = copied.SizeBytes,
            Sha256 = copied.Sha256,
            Metadata = metadata,
        };
    }

    /// <summary>No recorded hash to check it against, so unlike a bundle a damaged one is imported
    /// with a warning: getting a broken save in is how somebody looks at what is left of it.</summary>
    private LibraryManifest ImportBareContainer(string sourcePath, string directory, List<string> warnings)
    {
        var savePath = Path.Combine(directory, LibraryEntry.SaveFileName);
        var fileName = Path.GetFileName(sourcePath);

        var copied = CopyProving(sourcePath, savePath, fileName, progress: null);

        var slot = SaveSlotRef.ForFileName(fileName);
        var realm = slot?.Realm ?? SaveSlotRef.RealmForFileName(fileName);
        var metadata = SaveMetadataExtractor.Extract(savePath, slot?.Slot ?? 0, realm);

        if (metadata.ParseError is { } parseError)
        {
            warnings.Add($"{fileName} could not be read by this app ({parseError}). It was imported exactly as it is.");
        }
        else if (metadata.ChecksumValid == false)
        {
            warnings.Add($"{fileName} has a checksum the game will reject. It was imported exactly as it is, and importing does not repair that.");
        }

        return new LibraryManifest
        {
            SchemaVersion = LibraryManifest.CurrentSchemaVersion,
            Name = Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem ? stem : fileName,
            Note = null,
            CreatedUtc = DateTime.UtcNow,
            AppVersion = _appVersion,
            SourceFileName = fileName,
            SourceRealm = realm,
            SourceSlot = slot?.Slot ?? 0,
            SizeBytes = copied.SizeBytes,
            Sha256 = copied.Sha256,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Where one settings file sits inside an entry: ModConfigs\DvrmentConfs\current.json is kept
    /// at configs\DvrmentConfs\current.json. Null for a path that is not one of the ones that
    /// travel, rather than a guessed destination for a path this does not recognise.
    /// </summary>
    private static string? ConfigEntryPath(string entryDirectory, string relativePath)
    {
        if (ModConfigReader.PathBelowFolder(relativePath) is not { Length: > 0 } below)
        {
            return null;
        }

        var root = Path.Combine(entryDirectory, LibraryEntry.ConfigsFolderName);
        var candidate = Path.GetFullPath(Path.Combine(root, below));

        return CanonicalPath.IsInside(root, candidate) ? candidate : null;
    }

    /// <summary>
    /// Moves the settings an update is replacing into configs.previous, following save.previous.bin
    /// exactly: one generation, and the next update replaces it. False when the folder would not
    /// move, which is the caller's cue not to record a previous generation the folder does not hold.
    /// </summary>
    private static bool MoveConfigsAside(LibraryEntry entry)
    {
        try
        {
            TryDeleteDirectory(entry.PreviousConfigsPath);

            if (Directory.Exists(entry.ConfigsPath))
            {
                Directory.Move(entry.ConfigsPath, entry.PreviousConfigsPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the earlier settings back. False leaves the newer ones in place and configs.previous
    /// where it is: the next update clears it, and nothing is lost in the meantime.
    /// </summary>
    private static bool MoveConfigsBack(LibraryEntry entry)
    {
        try
        {
            if (!Directory.Exists(entry.PreviousConfigsPath))
            {
                TryDeleteDirectory(entry.ConfigsPath);
                return true;
            }

            TryDeleteDirectory(entry.ConfigsPath);
            Directory.Move(entry.PreviousConfigsPath, entry.ConfigsPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The settings in a save folder, or null when there is no folder to read them from.</summary>
    private static ModConfigSet? StoreConfigs(string entryDirectory, string? configsRoot)
        => string.IsNullOrWhiteSpace(configsRoot)
            ? null
            : CopyConfigsInto(entryDirectory, ModConfigReader.Read(configsRoot));

    /// <summary>
    /// Copies mod settings into an entry, proving each one the way the save itself is proved. One
    /// that cannot be proved is left out of the record and named in the note rather than throwing:
    /// losing the save because a mod rewrote its settings mid-copy would be the wrong trade.
    /// </summary>
    private static ModConfigSet CopyConfigsInto(string entryDirectory, ModConfigScan scan)
    {
        var set = new ModConfigSet { ReadTheFolder = scan.ReadTheFolder, Note = scan.Note };

        if (!scan.ReadTheFolder)
        {
            return set;
        }

        var missed = new List<string>();

        foreach (var file in scan.Files)
        {
            var destination = ConfigEntryPath(entryDirectory, file.RelativePath);
            if (destination is null)
            {
                missed.Add(file.RelativePath);
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                var copied = CopyProving(file.FullPath, destination, file.RelativePath, progress: null);

                set.Files.Add(new ModConfigFile
                {
                    RelativePath = file.RelativePath,
                    ModId = file.ModId,
                    SizeBytes = copied.SizeBytes,
                    Sha256 = copied.Sha256,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(destination);
                missed.Add(file.RelativePath);
            }
        }

        if (missed.Count > 0)
        {
            var line = "These mod settings were not kept with the save: " + string.Join(", ", missed) + ".";
            set.Note = string.IsNullOrWhiteSpace(set.Note) ? line : set.Note + " " + line;
        }

        return set;
    }

    /// <summary>A file that moves under the copy is copied again, and one that will not sit still
    /// throws rather than being recorded as sound.</summary>
    private static (long SizeBytes, string Sha256) CopyProving(
        string sourcePath,
        string destinationPath,
        string label,
        IProgress<string>? progress)
    {
        var info = new FileInfo(sourcePath);
        var expectedLength = info.Length;
        var expectedWrite = info.LastWriteTimeUtc;
        var problem = "it could not be read";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt == 2)
            {
                var refreshed = new FileInfo(sourcePath);
                if (!refreshed.Exists)
                {
                    break;
                }

                expectedLength = refreshed.Length;
                expectedWrite = refreshed.LastWriteTimeUtc;
            }

            progress?.Report(attempt == 1
                ? $"Copying {label} ({SlotCopyService.FormatSize(expectedLength)})"
                : $"Copying {label} again, it moved during the first copy");

            File.Copy(sourcePath, destinationPath, overwrite: true);

            progress?.Report($"Checking {label}");

            var source = new FileInfo(sourcePath);
            var copy = new FileInfo(destinationPath);

            if (!source.Exists)
            {
                problem = "it disappeared while it was being copied";
            }
            else if (source.Length != expectedLength || source.LastWriteTimeUtc != expectedWrite)
            {
                problem = $"it changed while it was being copied: it was {expectedLength} bytes and is now {source.Length} bytes";
            }
            else if (copy.Length != source.Length)
            {
                problem = $"the copy is {copy.Length} bytes and the file is {source.Length} bytes";
            }
            else
            {
                var sourceHash = Hashing.ComputeFileSha256(sourcePath);
                var copyHash = Hashing.ComputeFileSha256(destinationPath);

                if (HashComparer.Equals(sourceHash, copyHash))
                {
                    return (copy.Length, copyHash);
                }

                problem = "the copy does not match the file it was copied from";
            }
        }

        throw new IOException(
            $"{label} could not be copied: {problem}. Nothing was stored rather than storing a save that " +
            "cannot be shown to match. Close Steam, or wait for Steam Cloud to finish syncing, and try again.");
    }

    /// <summary>Goes last on a new entry, so its presence is what marks the entry finished.</summary>
    private static void WriteManifest(string directory, LibraryManifest manifest)
    {
        var path = Path.Combine(directory, LibraryEntry.ManifestFileName);
        var temp = path + ".tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, BackupJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Refuses anything that is not an entry folder directly inside the library root, and
    /// anything inside the save folder.</summary>
    private void EnsureOwnedEntry(LibraryEntry entry, string verb)
    {
        var target = TrimSeparators(Path.GetFullPath(entry.DirectoryPath));
        var root = TrimSeparators(LibraryRoot);
        var parent = Path.GetDirectoryName(target);

        if (string.IsNullOrEmpty(parent) || !HashComparer.Equals(TrimSeparators(parent), root))
        {
            throw new InvalidOperationException(
                $"Refusing to {verb} \"{target}\": it is not a library folder directly inside {root}.");
        }

        if (CanonicalPath.IsInside(SaveRoot, target))
        {
            throw new InvalidOperationException(
                $"Refusing to {verb} \"{target}\": it sits inside the save folder {SaveRoot}.");
        }
    }

    private string ResolveSlotPath(SaveSlotRef slot)
    {
        if (!slot.IsRealSlot)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Slot {0} is not a Rain World slot. The game has slots {1} to {2}.",
                    slot.Slot,
                    SaveSlotRef.MinSlot,
                    SaveSlotRef.MaxSlot),
                nameof(slot));
        }

        return Path.Combine(SaveRoot, slot.FileName);
    }

    private void EnsureGameNotRunning()
    {
        if (_gameDetector.IsGameRunning(out var processName))
        {
            throw new GameRunningException(string.IsNullOrWhiteSpace(processName) ? "Rain World" : processName);
        }
    }

    /// <summary>Clears away a folder an import claimed and then could not fill.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
