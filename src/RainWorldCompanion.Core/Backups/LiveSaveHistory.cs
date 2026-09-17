using System.Text;
using System.Text.Json;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;
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
        _time = timeProvider ?? TimeProvider.System;
        _snapshotter = new BackupService(
            _saveRoot,
            _historyRoot,
            new NullGameProcessDetector(),
            appVersion);
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

            var warnings = new List<string>();
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
            warnings.AddRange(Prune());
            return new LiveHistoryObservation(captured, warnings);
        }
    }

    public LiveHistoryView Read()
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

                entries.Add(new LiveHistoryEntry(
                    snapshot,
                    manifest.CapturedUtc,
                    manifest.Campaigns.ToArray()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
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

    public BackupSnapshot KeepAsBackup(LiveHistoryEntry entry, BackupService backups)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(backups);

        string campaigns = string.Join(
            ", ",
            entry.Campaigns.Select(campaign => SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName).Distinct());
        string label = campaigns.Length == 0 ? "Saved recent game save" : "Saved recent game save: " + campaigns;
        string note = "Kept from automatic live-save history captured "
                      + entry.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + ".";
        return backups.PreserveSnapshot(entry.Snapshot, label, note);
    }

    private LiveHistoryEntry Capture(
        Dictionary<CampaignKey, CampaignState> expected,
        IReadOnlyList<CampaignKey> changed,
        CancellationToken ct)
    {
        BackupSnapshot snapshot = _snapshotter.CreateBackup(
            "Recent live save",
            "Captured automatically after Rain World saved.",
            BackupKind.Manual,
            ct: ct);

        Dictionary<CampaignKey, CampaignState> copied = ReadCampaignState(snapshot.DirectoryPath);
        if (!SameState(expected, copied))
        {
            _snapshotter.DeleteBackup(snapshot);
            throw new IOException("the live campaign changed while its recent save was being copied");
        }

        var manifest = new LiveHistoryManifest
        {
            CapturedUtc = _time.GetUtcNow(),
            Campaigns = changed
                .Where(copied.ContainsKey)
                .Select(key => new LiveHistoryCampaign(
                    key.Realm,
                    key.Slot,
                    key.SlugcatId,
                    copied[key].Cycle))
                .ToList(),
        };
        try
        {
            WriteHistoryManifest(snapshot.DirectoryPath, manifest);
        }
        catch
        {
            try
            {
                _snapshotter.DeleteBackup(snapshot);
            }
            catch (Exception)
            {
            }

            throw;
        }

        return new LiveHistoryEntry(snapshot, manifest.CapturedUtc, manifest.Campaigns.ToArray());
    }

    private IReadOnlyList<string> Prune()
    {
        LiveHistoryView view = Read();
        var warnings = new List<string>(view.Warnings);
        var counts = new Dictionary<CampaignKey, int>();

        foreach (LiveHistoryEntry entry in view.Entries)
        {
            var retained = new List<LiveHistoryCampaign>();
            foreach (LiveHistoryCampaign campaign in entry.Campaigns)
            {
                var key = new CampaignKey(campaign.Realm, campaign.Slot, campaign.SlugcatId);
                counts.TryGetValue(key, out int count);
                if (count < RetainedPerCampaign)
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add("An older recent save could not be pruned: " + ex.Message);
            }
        }

        return warnings;
    }

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
                    state[key] = new CampaignState(Hashing.ComputeSha256(CampaignFile.ToBytes(slice)), cycle);
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

    private sealed record CampaignKey(SaveRealm Realm, int Slot, string SlugcatId);

    private sealed record CampaignState(string Hash, int? Cycle);
}
