// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Core.Backups;

public enum SlotAutoSyncState
{
    Off,
    Waiting,
    InSync,
    Synced,
    Conflict,
    Failed,
}

public sealed record SlotAutoSyncStatus(int Slot, SlotAutoSyncState State, string Text);

/// <summary>
/// Keeps a slot's local file (sav3) and its Rain Meadow online file (online_sav3) the same. Rain
/// Meadow reloads the save from disk when a lobby is joined and when it is left, and the game has
/// only one of the two files loaded at a time, so copying the file the game last wrote onto the
/// other one means the next session starts from it.
/// </summary>
public sealed class SlotAutoSync
{
    // A failed write already took a safety backup, so it is not retried on every poll.
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    // A sync the game asked for skips the two-check wait, except for a file written this recently.
    private static readonly TimeSpan JustWritten = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SlotCopyService _copies;
    private readonly string _statePath;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // The poll and a sync the game asked for run on different threads. One slot is copied at a time.
    private readonly object _checkLock = new();
    private readonly Dictionary<string, FileStamp> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (FileStamp Stamp, string Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SlotAutoSyncStatus> _status = new();
    private readonly Dictionary<int, (string Pair, DateTimeOffset At)> _failed = new();
    private StateFile _state;

    public SlotAutoSync(SlotCopyService copies, string statePath, TimeProvider? time = null)
    {
        _copies = copies ?? throw new ArgumentNullException(nameof(copies));
        _statePath = Path.GetFullPath(statePath);
        _time = time ?? TimeProvider.System;
        _state = ReadState(_statePath);
    }

    public static string DefaultStatePathFor(string saveRoot)
    {
        string full = Path.GetFullPath(saveRoot).ToUpperInvariant();
        string key = Hashing.ComputeSha256(Encoding.UTF8.GetBytes(full))[..16];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RainWorldCompanion",
            "autosync",
            key + ".json");
    }

    public bool IsEnabled(int slot)
    {
        lock (_gate)
        {
            return _state.Slots.TryGetValue(Key(slot), out SlotState? state) && state.Enabled;
        }
    }

    /// <summary>Turning a slot on forgets what it was last synced to, so the first check copies the
    /// newer file onto the older one.</summary>
    public void SetEnabled(int slot, bool enabled)
    {
        if (slot < SaveSlotRef.MinSlot || slot > SaveSlotRef.MaxSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        lock (_gate)
        {
            _state.Slots.TryGetValue(Key(slot), out SlotState? before);
            _state.Slots[Key(slot)] = new SlotState { Enabled = enabled };
            try
            {
                WriteState();
            }
            catch
            {
                if (before is null)
                {
                    _state.Slots.Remove(Key(slot));
                }
                else
                {
                    _state.Slots[Key(slot)] = before;
                }

                throw;
            }

            _status.Remove(slot);
            _failed.Remove(slot);
        }
    }

    public int[] EnabledSlots()
    {
        lock (_gate)
        {
            return Enumerable.Range(SaveSlotRef.MinSlot, SaveSlotRef.MaxSlot - SaveSlotRef.MinSlot + 1)
                .Where(slot => _state.Slots.TryGetValue(Key(slot), out SlotState? state) && state.Enabled)
                .ToArray();
        }
    }

    public SlotAutoSyncStatus StatusFor(int slot)
    {
        lock (_gate)
        {
            if (!(_state.Slots.TryGetValue(Key(slot), out SlotState? state) && state.Enabled))
            {
                return new SlotAutoSyncStatus(slot, SlotAutoSyncState.Off, "");
            }

            return _status.TryGetValue(slot, out SlotAutoSyncStatus? status)
                ? status
                : new SlotAutoSyncStatus(slot, SlotAutoSyncState.Waiting, "Waiting for the save files to settle.");
        }
    }

    /// <summary>Returns true when a file in the save folder was written.</summary>
    public bool Check(CancellationToken ct = default)
    {
        lock (_checkLock)
        {
            return CheckAll(ct);
        }
    }

    // The game is held until this returns, so files skip the two-check wait.
    public SlotAutoSyncStatus SyncNow(int slot, out bool wrote, CancellationToken ct = default)
    {
        wrote = false;
        if (slot < SaveSlotRef.MinSlot || slot > SaveSlotRef.MaxSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        if (!Monitor.TryEnter(_checkLock, TimeSpan.FromSeconds(3)))
        {
            return new SlotAutoSyncStatus(slot, SlotAutoSyncState.Waiting, "Autosync was already copying, so the game loaded the save as it was.");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            SlotState? state;
            lock (_gate)
            {
                if (!(_state.Slots.TryGetValue(Key(slot), out state) && state.Enabled))
                {
                    return new SlotAutoSyncStatus(slot, SlotAutoSyncState.Off, "");
                }
            }

            SlotAutoSyncStatus? status;
            try
            {
                status = CheckSlot(slot, state, ref wrote, ct, gameIsWaiting: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                status = new SlotAutoSyncStatus(slot, SlotAutoSyncState.Failed, "Autosync could not check this slot: " + ex.Message);
            }

            if (status is null)
            {
                return StatusFor(slot);
            }

            lock (_gate)
            {
                _status[slot] = status;
            }

            return status;
        }
        finally
        {
            Monitor.Exit(_checkLock);
        }
    }

    private bool CheckAll(CancellationToken ct)
    {
        bool wrote = false;

        for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
        {
            ct.ThrowIfCancellationRequested();

            SlotState? state;
            lock (_gate)
            {
                if (!(_state.Slots.TryGetValue(Key(slot), out state) && state.Enabled))
                {
                    continue;
                }
            }

            SlotAutoSyncStatus? status;
            try
            {
                status = CheckSlot(slot, state, ref wrote, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                status = new SlotAutoSyncStatus(slot, SlotAutoSyncState.Failed, "Autosync could not check this slot: " + ex.Message);
            }

            if (status is not null)
            {
                lock (_gate)
                {
                    _status[slot] = status;
                }
            }
        }

        return wrote;
    }

    /// <summary>Null keeps the status shown before, which is what a check that is still waiting for a
    /// file to settle should do.</summary>
    private SlotAutoSyncStatus? CheckSlot(int slot, SlotState state, ref bool wrote, CancellationToken ct, bool gameIsWaiting = false)
    {
        string? last;
        lock (_gate)
        {
            last = state.LastSyncedSha256;
        }

        var localRef = new SaveSlotRef(SaveRealm.Local, slot);
        var onlineRef = new SaveSlotRef(SaveRealm.Online, slot);
        string localPath = Path.Combine(_copies.SaveRoot, localRef.FileName);
        string onlinePath = Path.Combine(_copies.SaveRoot, onlineRef.FileName);

        // The game writes a save in more than one step, so a file is only read once two checks in a
        // row found it unchanged.
        bool localSettled = Settled(localPath, out FileStamp? localStamp) || (gameIsWaiting && Old(localStamp));
        bool onlineSettled = Settled(onlinePath, out FileStamp? onlineStamp) || (gameIsWaiting && Old(onlineStamp));
        if (!localSettled || !onlineSettled)
        {
            return gameIsWaiting
                ? new SlotAutoSyncStatus(slot, SlotAutoSyncState.Waiting, "A save file was still being written, so the game loaded it as it was.")
                : null;
        }

        if (localStamp is null && onlineStamp is null)
        {
            return new SlotAutoSyncStatus(slot, SlotAutoSyncState.InSync, "Neither file exists yet.");
        }

        string? localHash = localStamp is null ? null : HashOf(localPath, localStamp);
        string? onlineHash = onlineStamp is null ? null : HashOf(onlinePath, onlineStamp);

        if (localHash is not null && string.Equals(localHash, onlineHash, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(last, localHash, StringComparison.OrdinalIgnoreCase))
            {
                RecordSynced(slot, localHash);
            }

            lock (_gate)
            {
                return _status.TryGetValue(slot, out SlotAutoSyncStatus? shown) && shown.State == SlotAutoSyncState.Synced
                    ? shown
                    : new SlotAutoSyncStatus(slot, SlotAutoSyncState.InSync, "Local and online are the same.");
            }
        }

        SyncDirection direction = Decide(
            localHash,
            onlineHash,
            last,
            localStamp?.LastWriteUtc,
            onlineStamp?.LastWriteUtc);

        string localName = localRef.FileName;
        string onlineName = onlineRef.FileName;

        switch (direction)
        {
            case SyncDirection.Conflict:
                return new SlotAutoSyncStatus(
                    slot,
                    SlotAutoSyncState.Conflict,
                    $"{localName} and {onlineName} both changed since the last sync. Copy one onto the other with Copy Slot, and autosync carries on from there.");
            case SyncDirection.SourceMissing:
                return new SlotAutoSyncStatus(
                    slot,
                    SlotAutoSyncState.Conflict,
                    $"{(localHash is null ? localName : onlineName)} was removed after the last sync, so autosync left {(localHash is null ? onlineName : localName)} as it is.");
            case SyncDirection.None:
                return null;
        }

        bool toOnline = direction == SyncDirection.LocalToOnline;
        SaveSlotRef from = toOnline ? localRef : onlineRef;
        SaveSlotRef to = toOnline ? onlineRef : localRef;
        string sourceHash = (toOnline ? localHash : onlineHash)!;
        string? targetHash = toOnline ? onlineHash : localHash;

        if (Behind(from, to) is { } behind)
        {
            return new SlotAutoSyncStatus(slot, SlotAutoSyncState.Conflict, behind);
        }

        string pair = sourceHash + ">" + (targetHash ?? "");
        lock (_gate)
        {
            if (_failed.TryGetValue(slot, out var failed)
                && failed.Pair == pair
                && _time.GetUtcNow() - failed.At < RetryAfterFailure)
            {
                return null;
            }
        }

        // SetEnabled replaces the slot's state, so a switch turned off or on again since this check
        // began stops the write. So does a game that stopped waiting for it.
        bool StillWanted()
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            lock (_gate)
            {
                return _state.Slots.TryGetValue(Key(slot), out SlotState? now) && ReferenceEquals(now, state) && now.Enabled;
            }
        }

        SlotCopyResult result = _copies.SyncSlot(from, to, sourceHash, targetHash, StillWanted, ct);
        wrote |= result.LiveFolderModified;

        // The target was just written, so its stamp is stale. It reads as settled on the next check
        // only if nothing writes it again.
        lock (_gate)
        {
            // The copy can keep the source's length and time, so the cached hash goes too.
            _seen.Remove(Path.Combine(_copies.SaveRoot, to.FileName));
            _hashes.Remove(Path.Combine(_copies.SaveRoot, to.FileName));
        }

        if (!result.Success)
        {
            lock (_gate)
            {
                _failed[slot] = (pair, _time.GetUtcNow());
            }

            return new SlotAutoSyncStatus(
                slot,
                SlotAutoSyncState.Failed,
                result.Errors.Count > 0 ? result.Errors[0] : $"Autosync could not copy {from.FileName} onto {to.FileName}.");
        }

        RecordSynced(slot, sourceHash);
        string time = _time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return new SlotAutoSyncStatus(
            slot,
            SlotAutoSyncState.Synced,
            $"Copied {from.FileName} onto {to.FileName} at {time}.");
    }

    public enum SyncDirection
    {
        None,
        LocalToOnline,
        OnlineToLocal,
        Conflict,
        SourceMissing,
    }

    /// <summary>
    /// Which file wins. With nothing recorded, the newer file does. Otherwise the one file that
    /// changed since the last sync does, and two changed files are a conflict that is left alone.
    /// </summary>
    internal static SyncDirection Decide(
        string? localHash,
        string? onlineHash,
        string? lastSynced,
        DateTime? localWriteUtc,
        DateTime? onlineWriteUtc)
    {
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        if (localHash is null && onlineHash is null)
        {
            return SyncDirection.None;
        }

        if (Same(localHash, onlineHash))
        {
            return SyncDirection.None;
        }

        if (lastSynced is null)
        {
            if (onlineHash is null)
            {
                return SyncDirection.LocalToOnline;
            }

            if (localHash is null)
            {
                return SyncDirection.OnlineToLocal;
            }

            return localWriteUtc >= onlineWriteUtc ? SyncDirection.LocalToOnline : SyncDirection.OnlineToLocal;
        }

        bool localChanged = !Same(localHash, lastSynced);
        bool onlineChanged = !Same(onlineHash, lastSynced);

        if (localChanged && onlineChanged)
        {
            return SyncDirection.Conflict;
        }

        if (localChanged)
        {
            return localHash is null ? SyncDirection.SourceMissing : SyncDirection.LocalToOnline;
        }

        if (onlineChanged)
        {
            return onlineHash is null ? SyncDirection.SourceMissing : SyncDirection.OnlineToLocal;
        }

        return SyncDirection.None;
    }

    /// <summary>
    /// A file that is behind the other one is not copied over it. That happens when the game loaded
    /// a file before autosync had updated it, then saved: copying that save on would undo the
    /// session in the other file.
    /// </summary>
    private string? Behind(SaveSlotRef from, SaveSlotRef to)
    {
        SlotSide source = _copies.ReadSide(from, includeCampaigns: true);
        SlotSide target = _copies.ReadSide(to, includeCampaigns: true);

        if (!target.Exists || target.Metadata is not { ParseError: null } targetMeta)
        {
            return null;
        }

        IReadOnlyList<CampaignSummary> sourceCampaigns = source.Metadata?.Campaigns ?? Array.Empty<CampaignSummary>();

        foreach (CampaignSummary campaign in targetMeta.Campaigns)
        {
            CampaignSummary? match = sourceCampaigns.FirstOrDefault(c =>
                string.Equals(c.SlugcatId, campaign.SlugcatId, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return $"{to.FileName} has a {campaign.DisplayName} campaign that {from.FileName} does not, so autosync did not copy over it. Copy one onto the other with Copy Slot, and autosync carries on from there.";
            }

            if (match.CycleNum < campaign.CycleNum)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is at {1} cycle {2} and {3} is at cycle {4}, so autosync did not copy the older save over the newer one. Copy one onto the other with Copy Slot, and autosync carries on from there.",
                    from.FileName,
                    campaign.DisplayName,
                    match.CycleNum,
                    to.FileName,
                    campaign.CycleNum);
            }
        }

        return null;
    }

    private bool Settled(string path, out FileStamp? stamp)
    {
        var info = new FileInfo(path);
        stamp = info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;

        lock (_gate)
        {
            bool known = _seen.TryGetValue(path, out FileStamp? before);
            if (stamp is null)
            {
                _seen.Remove(path);
                return !known;
            }

            _seen[path] = stamp;
            return known && before == stamp;
        }
    }

    private bool Old(FileStamp? stamp)
        => stamp is null || _time.GetUtcNow().UtcDateTime - stamp.LastWriteUtc >= JustWritten;

    private string HashOf(string path, FileStamp stamp)
    {
        lock (_gate)
        {
            if (_hashes.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                return cached.Hash;
            }
        }

        string hash = Hashing.ComputeFileSha256(path);
        lock (_gate)
        {
            _hashes[path] = (stamp, hash);
        }

        return hash;
    }

    private void RecordSynced(int slot, string hash)
    {
        lock (_gate)
        {
            // Turned off while this check ran: leave it off.
            if (_state.Slots.TryGetValue(Key(slot), out SlotState? state) && state.Enabled)
            {
                state.LastSyncedSha256 = hash;
                _failed.Remove(slot);
                WriteState();
            }
        }
    }

    private static string Key(int slot) => slot.ToString(CultureInfo.InvariantCulture);

    private static StateFile ReadState(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), JsonOptions) ?? new StateFile();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable state file reads as every slot off, which writes nothing.
        }

        return new StateFile();
    }

    private void WriteState()
    {
        string? directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = _statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(temp, _statePath, overwrite: true);
    }

    private sealed record FileStamp(long Length, DateTime LastWriteUtc);

    private sealed class StateFile
    {
        public Dictionary<string, SlotState> Slots { get; set; } = new();
    }

    private sealed class SlotState
    {
        public bool Enabled { get; set; }

        public string? LastSyncedSha256 { get; set; }
    }
}
