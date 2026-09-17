using System.Text;
using System.Text.Json;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Backups;

public sealed record LiveHistoryCampaign(
    SaveRealm Realm,
    int Slot,
    string SlugcatId,
    int? Cycle);

public sealed record LiveHistoryEntry(
    BackupSnapshot Snapshot,
    DateTimeOffset CapturedUtc,
    IReadOnlyList<LiveHistoryCampaign> Campaigns);

public sealed record LiveHistoryView(
    IReadOnlyList<LiveHistoryEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record LiveHistoryObservation(
    LiveHistoryEntry? Captured,
    IReadOnlyList<string> Warnings);

internal sealed class LiveHistoryManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public DateTimeOffset CapturedUtc { get; set; }

    public List<LiveHistoryCampaign> Campaigns { get; set; } = new();
}

public sealed class LiveSaveHistory
{
    public const int RetainedPerCampaign = 5;

    private const string HistoryManifestSuffix = ".live-history.json";

    private readonly string _saveRoot;
    private readonly string _historyRoot;
    private readonly string _appVersion;
    private readonly TimeProvider _time;
    private readonly BackupService _snapshotter;
    private readonly object _gate = new();

    private Dictionary<CampaignKey, CampaignState>? _baseline;
    private Dictionary<CampaignKey, CampaignState>? _pending;

    public LiveSaveHistory(
        string saveRoot,
        string historyRoot,
        string appVersion,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            throw new ArgumentException("Save root must not be empty.", nameof(saveRoot));
        }

        if (string.IsNullOrWhiteSpace(historyRoot))
        {
            throw new ArgumentException("History root must not be empty.", nameof(historyRoot));
        }

        _saveRoot = Path.GetFullPath(saveRoot);
        _historyRoot = Path.GetFullPath(historyRoot);
        _appVersion = appVersion ?? "";
        _time = timeProvider ?? TimeProvider.System;
        _snapshotter = new BackupService(
            _saveRoot,
            _historyRoot,
            new NullGameProcessDetector(),
            _appVersion);
    }

    public string HistoryRoot => _historyRoot;

    public static string DefaultRootFor(string saveRoot)
    {
        string full = Path.GetFullPath(saveRoot).ToUpperInvariant();
        string key = Hashing.ComputeSha256(Encoding.UTF8.GetBytes(full))[..16];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RainWorldCompanion",
            "live-history",
            key);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _baseline = null;
            _pending = null;
        }
    }

    public LiveHistoryObservation Observe(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            Dictionary<CampaignKey, CampaignState> current = ReadCampaignState(_saveRoot);

            if (_baseline is null)
            {
                _baseline = current;
                _pending = null;
                return new LiveHistoryObservation(null, Array.Empty<string>());
            }

            List<CampaignKey> changed = ChangedCampaigns(_baseline, current);
            if (changed.Count == 0)
            {
                _pending = null;
                return new LiveHistoryObservation(null, Array.Empty<string>());
            }

            if (_pending is null || !SameState(_pending, current))
            {
                _pending = current;
                return new LiveHistoryObservation(null, Array.Empty<string>());
            }

            LiveHistoryView retainedHistory = Prune();
            var warnings = new List<string>(retainedHistory.Warnings);
            changed = changed
                .Where(key => !HasRecoveryPoint(retainedHistory, key, current[key].Cycle))
                .ToList();
            if (changed.Count == 0)
            {
                _baseline = current;
                _pending = null;
                return new LiveHistoryObservation(null, warnings);
            }

            LiveHistoryEntry? captured;
            try
            {
                captured = Capture(current, changed, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add("The recent save could not be captured: " + ex.Message);
                return new LiveHistoryObservation(null, warnings);
            }

            _baseline = current;
            _pending = null;
            warnings.AddRange(Prune().Warnings);
            return new LiveHistoryObservation(captured, warnings);
        }
    }

    public LiveHistoryView Read()
    {
        lock (_gate)
        {
            return Prune();
        }
    }

    private LiveHistoryView ReadUnpruned()
    {
        var entries = new List<LiveHistoryEntry>();
        var warnings = new List<string>();

        if (!Directory.Exists(_historyRoot))
        {
            return new LiveHistoryView(entries, warnings);
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_historyRoot).ToArray();
        }
        catch (Exception ex)
        {
            return new LiveHistoryView(entries, new[] { "Recent saves could not be listed: " + ex.Message });
        }

        foreach (string directory in directories)
        {
            string path = HistoryManifestPath(directory);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                LiveHistoryManifest? manifest = JsonSerializer.Deserialize<LiveHistoryManifest>(
                    File.ReadAllText(path),
                    BackupJson.Options);
                if (manifest is null)
                {
                    throw new InvalidDataException("the history manifest is empty");
                }

                if (manifest.SchemaVersion > LiveHistoryManifest.CurrentSchemaVersion)
                {
                    warnings.Add(Path.GetFileName(directory) + " uses a newer recent-save format and was skipped.");
                    continue;
                }

                BackupSnapshot snapshot = BackupSnapshot.Load(directory);
                if (!snapshot.IsComplete)
                {
                    warnings.Add(Path.GetFileName(directory) + " is incomplete and was skipped.");
                    continue;
                }

                if (!IsCampaignSnapshot(snapshot))
                {
                    entries.AddRange(MigrateWholeSaveEntry(snapshot, manifest));
                    continue;
                }

                entries.Add(new LiveHistoryEntry(
                    snapshot,
                    manifest.CapturedUtc,
                    manifest.Campaigns.ToArray()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or InvalidDataException or SaveContainerException)
            {
                warnings.Add(Path.GetFileName(directory) + " could not be read and was skipped: " + ex.Message);
            }
        }

        entries.Sort(static (left, right) =>
        {
            int byTime = right.CapturedUtc.CompareTo(left.CapturedUtc);
            return byTime != 0
                ? byTime
                : StringComparer.OrdinalIgnoreCase.Compare(right.Snapshot.Id, left.Snapshot.Id);
        });
        return new LiveHistoryView(entries, warnings);
    }

    public CampaignMovePlan PlanRestore(LiveHistoryEntry entry, BackupService backups)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(backups);

        (LiveHistoryCampaign campaign, CampaignSlice slice) = ReadStoredCampaign(entry);
        return backups.SlotWriter.PlanPutCampaign(
            new SaveSlotRef(campaign.Realm, campaign.Slot),
            slice);
    }

    public SaveWriteResult Restore(
        LiveHistoryEntry entry,
        BackupService backups,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        ModListSnapshot? modsBefore = null)
    {
        CampaignMovePlan plan = PlanRestore(entry, backups);
        return backups.SlotWriter.Write(plan, progress, ct, extras: null, modsBefore);
    }

    public LibraryEntry KeepInLibrary(LiveHistoryEntry entry, SaveLibrary library)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(library);

        (LiveHistoryCampaign campaign, CampaignSlice slice) = ReadStoredCampaign(entry);
        string campaignName = SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName;
        string name = campaign.Cycle is { } cycle
            ? $"{campaignName}, cycle {cycle}"
            : campaignName + " recovery";
        string note = "Kept from automatic live-save history captured "
                      + entry.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + ".";
        return library.StoreCampaignFrom(
            slice,
            new SaveSlotRef(campaign.Realm, campaign.Slot).FileName,
            campaign.Realm,
            campaign.Slot,
            name,
            note,
            entry.Snapshot.Manifest?.Mods);
    }

    private LiveHistoryEntry Capture(
        Dictionary<CampaignKey, CampaignState> expected,
        IReadOnlyList<CampaignKey> changed,
        CancellationToken ct)
    {
        var captured = new List<LiveHistoryEntry>();
        try
        {
            foreach (CampaignKey key in changed)
            {
                ct.ThrowIfCancellationRequested();
                captured.Add(CaptureCampaign(key, expected[key]));
            }

            Dictionary<CampaignKey, CampaignState> checkedLive = ReadCampaignState(_saveRoot);
            if (!SameState(expected, checkedLive))
            {
                throw new IOException("the live campaign changed while its recent save was being copied");
            }

            return captured[0];
        }
        catch
        {
            foreach (LiveHistoryEntry entry in captured)
            {
                DeleteEntry(entry);
            }

            throw;
        }
    }

    private LiveHistoryEntry CaptureCampaign(
        CampaignKey key,
        CampaignState state,
        DateTimeOffset? capturedAt = null)
    {
        string directory = TimestampedFolders.Create(
            _historyRoot,
            BackupService.ClaimFileName,
            "recent campaign folder");
        string campaignPath = Path.Combine(directory, LibraryEntry.CampaignFileName);
        File.WriteAllBytes(campaignPath, state.Bytes);

        DateTimeOffset capturedUtc = capturedAt ?? _time.GetUtcNow();
        var source = new SaveSlotRef(key.Realm, key.Slot);
        SlotMetadata extracted = SaveMetadataExtractor.FromPayload(
            CampaignFile.ToPayload(state.Slice),
            source.FileName,
            key.Slot,
            key.Realm);
        var slot = new SlotMetadata
        {
            Slot = key.Slot,
            FileName = source.FileName,
            Realm = key.Realm,
            ChecksumValid = extracted.ChecksumValid,
            Campaigns = extracted.Campaigns,
            ParseError = extracted.ParseError,
            RecordCount = extracted.RecordCount,
        };
        var snapshotManifest = new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,
            AppVersion = _appVersion,
            CreatedUtc = capturedUtc.UtcDateTime,
            Label = "Recent live save",
            Note = "Captured automatically after Rain World saved.",
            Kind = BackupKind.Manual,
            MetadataVersion = SaveMetadataExtractor.Version,
        };
        snapshotManifest.Files.Add(new ManifestFileEntry(
            LibraryEntry.CampaignFileName,
            state.Bytes.LongLength,
            state.Hash,
            capturedUtc.UtcDateTime));
        snapshotManifest.Slots.Add(slot);

        TimestampedFolders.ReleaseClaim(directory, BackupService.ClaimFileName);
        WriteSnapshotManifest(directory, snapshotManifest);

        var historyManifest = new LiveHistoryManifest
        {
            CapturedUtc = capturedUtc,
            Campaigns = [new LiveHistoryCampaign(key.Realm, key.Slot, key.SlugcatId, state.Cycle)],
        };
        try
        {
            WriteHistoryManifest(directory, historyManifest);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }

        return new LiveHistoryEntry(
            BackupSnapshot.Load(directory),
            capturedUtc,
            historyManifest.Campaigns.ToArray());
    }

    private IReadOnlyList<LiveHistoryEntry> MigrateWholeSaveEntry(
        BackupSnapshot snapshot,
        LiveHistoryManifest manifest)
    {
        var migrated = new List<LiveHistoryEntry>();
        try
        {
            foreach (LiveHistoryCampaign campaign in manifest.Campaigns)
            {
                var key = new CampaignKey(campaign.Realm, campaign.Slot, campaign.SlugcatId);
                string sourcePath = Path.Combine(
                    snapshot.DirectoryPath,
                    new SaveSlotRef(campaign.Realm, campaign.Slot).FileName);
                CampaignSlice slice = CampaignFile.ReadFrom(sourcePath, campaign.SlugcatId)
                    ?? throw new InvalidDataException(
                        Path.GetFileName(sourcePath) + " does not contain " + campaign.SlugcatId + ".");
                byte[] bytes = CampaignFile.ToBytes(slice);
                migrated.Add(CaptureCampaign(
                    key,
                    new CampaignState(Hashing.ComputeSha256(bytes), campaign.Cycle, slice, bytes),
                    manifest.CapturedUtc));
            }

            if (migrated.Count == 0)
            {
                throw new InvalidDataException("the recent-save entry names no campaign");
            }

            _snapshotter.DeleteBackup(snapshot);
            DeleteHistoryManifest(snapshot.DirectoryPath);
            return migrated;
        }
        catch
        {
            foreach (LiveHistoryEntry entry in migrated)
            {
                DeleteEntry(entry);
            }

            throw;
        }
    }

    private static bool IsCampaignSnapshot(BackupSnapshot snapshot)
        => snapshot.Manifest is { Files.Count: 1 } manifest
           && string.Equals(
               manifest.Files[0].RelativePath,
               LibraryEntry.CampaignFileName,
               StringComparison.OrdinalIgnoreCase);

    private static (LiveHistoryCampaign Campaign, CampaignSlice Slice) ReadStoredCampaign(
        LiveHistoryEntry entry)
    {
        LiveHistoryCampaign campaign = entry.Campaigns.Count == 1
            ? entry.Campaigns[0]
            : throw new InvalidDataException("A recent save must contain exactly one campaign.");
        BackupManifest manifest = entry.Snapshot.Manifest
            ?? throw new InvalidDataException("The recent save is incomplete.");
        ManifestFileEntry file = manifest.Files.Count == 1
            ? manifest.Files[0]
            : throw new InvalidDataException("A recent save must contain exactly one file.");
        if (!string.Equals(file.RelativePath, LibraryEntry.CampaignFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The recent save is not stored as a campaign.");
        }

        string path = Path.Combine(entry.Snapshot.DirectoryPath, LibraryEntry.CampaignFileName);
        if (!Hashing.FileMatchesHash(path, file.Sha256))
        {
            throw new InvalidDataException("The recent campaign does not match its checksum.");
        }

        CampaignSlice slice = CampaignFile.Read(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("The recent campaign could not be read.");
        if (!string.Equals(slice.SlugcatId, campaign.SlugcatId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored campaign does not match its recent-save record.");
        }

        return (campaign, slice);
    }

    private LiveHistoryView Prune()
    {
        LiveHistoryView view = ReadUnpruned();
        var warnings = new List<string>(view.Warnings);
        var counts = new Dictionary<CampaignKey, int>();
        var retainedEntries = new List<LiveHistoryEntry>();
        var oldestByCycle = new Dictionary<CampaignCycle, string>();

        foreach (LiveHistoryEntry entry in view.Entries)
        {
            foreach (LiveHistoryCampaign campaign in entry.Campaigns)
            {
                if (campaign.Cycle is { } cycle)
                {
                    var key = new CampaignKey(campaign.Realm, campaign.Slot, campaign.SlugcatId);
                    oldestByCycle[new CampaignCycle(key, cycle)] = entry.Snapshot.Id;
                }
            }
        }

        foreach (LiveHistoryEntry entry in view.Entries)
        {
            var retained = new List<LiveHistoryCampaign>();
            foreach (LiveHistoryCampaign campaign in entry.Campaigns)
            {
                var key = new CampaignKey(campaign.Realm, campaign.Slot, campaign.SlugcatId);
                counts.TryGetValue(key, out int count);
                bool isOldestForCycle = campaign.Cycle is not { } cycle
                                        || StringComparer.OrdinalIgnoreCase.Equals(
                                            oldestByCycle[new CampaignCycle(key, cycle)],
                                            entry.Snapshot.Id);
                if (count < RetainedPerCampaign && isOldestForCycle)
                {
                    retained.Add(campaign);
                    counts[key] = count + 1;
                }
            }

            try
            {
                if (retained.Count == 0)
                {
                    _snapshotter.DeleteBackup(entry.Snapshot);
                    DeleteHistoryManifest(entry.Snapshot.DirectoryPath);
                }
                else if (retained.Count != entry.Campaigns.Count)
                {
                    WriteHistoryManifest(entry.Snapshot.DirectoryPath, new LiveHistoryManifest
                    {
                        CapturedUtc = entry.CapturedUtc,
                        Campaigns = retained,
                    });
                }

                if (retained.Count > 0)
                {
                    retainedEntries.Add(entry with { Campaigns = retained.ToArray() });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add("An older recent save could not be pruned: " + ex.Message);
                if (retained.Count > 0)
                {
                    retainedEntries.Add(entry with { Campaigns = retained.ToArray() });
                }
            }
        }

        return new LiveHistoryView(retainedEntries, warnings);
    }

    private static bool HasRecoveryPoint(
        LiveHistoryView history,
        CampaignKey key,
        int? cycle)
        => cycle is { } knownCycle && history.Entries.Any(entry => entry.Campaigns.Any(campaign =>
            campaign.Realm == key.Realm
            && campaign.Slot == key.Slot
            && string.Equals(campaign.SlugcatId, key.SlugcatId, StringComparison.Ordinal)
            && campaign.Cycle == knownCycle));

    private static Dictionary<CampaignKey, CampaignState> ReadCampaignState(string root)
    {
        var state = new Dictionary<CampaignKey, CampaignState>();
        if (!Directory.Exists(root))
        {
            return state;
        }

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            SaveSlotRef? slot = SaveSlotRef.ForFileName(Path.GetFileName(path));
            if (slot is null)
            {
                continue;
            }

            try
            {
                SaveEditSession session = SaveEditSession.Open(path);
                var metadata = SaveMetadataExtractor.Extract(path, slot.Slot, slot.Realm);
                var cycles = metadata.Campaigns.ToDictionary(
                    campaign => campaign.SlugcatId,
                    campaign => campaign.DisplayCycleNum,
                    StringComparer.Ordinal);

                foreach (CampaignRecordRef campaign in session.Campaigns)
                {
                    CampaignSlice? slice = session.TakeCampaign(campaign.SlugcatId);
                    if (slice is null)
                    {
                        continue;
                    }

                    var key = new CampaignKey(slot.Realm, slot.Slot, campaign.SlugcatId);
                    cycles.TryGetValue(campaign.SlugcatId, out int? cycle);
                    byte[] bytes = CampaignFile.ToBytes(slice);
                    state[key] = new CampaignState(Hashing.ComputeSha256(bytes), cycle, slice, bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SaveContainerException)
            {
            }
        }

        return state;
    }

    private static List<CampaignKey> ChangedCampaigns(
        IReadOnlyDictionary<CampaignKey, CampaignState> baseline,
        IReadOnlyDictionary<CampaignKey, CampaignState> current)
    {
        var changed = new List<CampaignKey>();
        foreach ((CampaignKey key, CampaignState value) in current)
        {
            if (!baseline.TryGetValue(key, out CampaignState? previous)
                || !string.Equals(previous.Hash, value.Hash, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    private static bool SameState(
        IReadOnlyDictionary<CampaignKey, CampaignState> left,
        IReadOnlyDictionary<CampaignKey, CampaignState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach ((CampaignKey key, CampaignState value) in left)
        {
            if (!right.TryGetValue(key, out CampaignState? other)
                || !string.Equals(value.Hash, other.Hash, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteHistoryManifest(string directory, LiveHistoryManifest manifest)
    {
        string path = HistoryManifestPath(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, BackupJson.Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static void WriteSnapshotManifest(string directory, BackupManifest manifest)
    {
        string path = Path.Combine(directory, BackupSnapshot.ManifestFileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, BackupJson.Options));
        File.Move(temporary, path, overwrite: true);
    }

    private static string HistoryManifestPath(string directory)
    {
        string root = Path.GetDirectoryName(directory)
                      ?? throw new InvalidOperationException("The recent-save folder has no parent.");
        return Path.Combine(root, Path.GetFileName(directory) + HistoryManifestSuffix);
    }

    private static void DeleteHistoryManifest(string directory)
    {
        string path = HistoryManifestPath(directory);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void DeleteEntry(LiveHistoryEntry entry)
    {
        try
        {
            _snapshotter.DeleteBackup(entry.Snapshot);
        }
        finally
        {
            DeleteHistoryManifest(entry.Snapshot.DirectoryPath);
        }
    }

    private sealed record CampaignKey(SaveRealm Realm, int Slot, string SlugcatId);

    private sealed record CampaignCycle(CampaignKey Campaign, int Cycle);

    private sealed record CampaignState(string Hash, int? Cycle, CampaignSlice Slice, byte[] Bytes);
}
