// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

/// <summary>The index is what the session works from, because it stays put while a field is edited.
/// The slugcat id is carried alongside for a caller to name the campaign it changed.</summary>
public sealed record CampaignRecordRef(int RecordIndex, string SlugcatId);

/// <summary>How much of a slot goes when it is deleted.</summary>
public enum SlotDeleteDepth
{
    /// <summary>The campaigns only. The map and the progression record stay, so the slot still
    /// remembers every shelter found, every ending seen and every tutorial shown.</summary>
    Campaigns,

    /// <summary>The campaigns and the map, the records the whole slot shares included. The
    /// progression record stays.</summary>
    CampaignsAndMap,

    /// <summary>All of it, including the copy of the old data the game keeps beside it in the same
    /// file.</summary>
    Everything,
}

public sealed record SlotDeleteReport(
    IReadOnlyList<string> Campaigns,
    int MapsRemoved,
    int OtherRecordsRemoved = 0,
    bool ClearedTheGamesOwnCopy = false)
{
    public bool TookNothing =>
        Campaigns.Count == 0 && MapsRemoved == 0 && OtherRecordsRemoved == 0 && !ClearedTheGamesOwnCopy;
}

/// <param name="Occurrence">Which of the fields sharing this key it is, counting from zero.</param>
/// <param name="IsFlag">True when the field is a bare token that means true by being present.</param>
public sealed record RawField(string Key, string? Value, int Occurrence, bool IsFlag);

/// <summary>
/// One slot file open for editing. A payload is never rebuilt from a parsed model: each edit
/// replaces the characters of one field and leaves the rest of the file alone, because the game
/// keeps save strings it did not recognise and writes them back untouched. A session also refuses to
/// open a save whose digest is already wrong, or a fresh digest would hide the damage.
/// </summary>
public sealed class SaveEditSession
{
    private const string SaveKey = "save";
    private const string SaveStateHeader = "SAVE STATE";
    private const string SlugcatField = "SAV STATE NUMBER";

    /// <summary>The copy of the previous save the game keeps in the same file. PlayerProgression
    /// writes it through BackUpSave and never reads it back.</summary>
    private const string GameBackupKey = "save__Backup";

    private const string EmptiedKey = "slot|emptied";

    private const string MapKey = "slot|map";

    private static readonly DelimitedFields Fields = DelimitedFields.Record;

    private readonly ContainerText _container;
    private readonly string _originalPayload;
    private readonly Dictionary<string, TrackedChange> _changes = new(StringComparer.Ordinal);
    private readonly HashSet<int> _touchedRecords = new();

    /// <summary>Records a campaign splice put in, and took out, so the plan can check it did only that.</summary>
    private readonly List<string> _recordsWritten = new();

    private readonly List<string> _recordsRemoved = new();

    /// <summary>Container entries other than "save" this write is meant to clear. The plan holds the
    /// write to exactly this list, so no other entry can be touched by accident.</summary>
    private readonly HashSet<string> _entriesToClear = new(StringComparer.Ordinal);

    private string _payload;
    private int _changeOrder;
    private int _splices;

    private SaveEditSession(string filePath, string fileSha256, ContainerText container, string payload)
    {
        FilePath = filePath;
        FileSha256 = fileSha256;
        _container = container;
        _originalPayload = payload;
        _payload = payload;
    }

    public string FilePath { get; }

    /// <summary>SHA-256 of the file as it was when the session opened. The game or Steam Cloud can
    /// put different bytes on disk while a dialog is open, so the write refuses when the file no
    /// longer hashes to this.</summary>
    public string FileSha256 { get; }

    /// <summary>The payload with every edit so far applied.</summary>
    public string Payload => _payload;

    public bool IsDirty =>
        !string.Equals(_payload, _originalPayload, StringComparison.Ordinal) || _entriesToClear.Count > 0;

    /// <summary>One line per thing changed, in the order it was first touched. A field edited over
    /// and over, which is what typing into a box does, reads as the one move it made.</summary>
    public IReadOnlyList<string> Changes => _changes.Values
        .OrderBy(change => change.Order)
        .Select(change => change.Describe())
        .ToArray();

    public event EventHandler? Changed;

    /// <summary>In the order the payload stores them.</summary>
    public IReadOnlyList<CampaignRecordRef> Campaigns
    {
        get
        {
            var campaigns = new List<CampaignRecordRef>();
            int index = 0;

            foreach (RecordSpan record in SavePayloadReader.EnumerateRecords(_payload))
            {
                if (record.HeaderIs(SaveStateHeader))
                {
                    campaigns.Add(new CampaignRecordRef(index, Fields.GetValue(record.Body(), SlugcatField) ?? ""));
                }

                index++;
            }

            return campaigns;
        }
    }

    /// <exception cref="SaveContainerException">The file cannot be read, or is not safe to edit.</exception>
    public static SaveEditSession Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SaveContainerException("No save file path was given.");
        }

        byte[] bytes;
        try
        {
            bytes = ReadAllBytesShared(path);
        }
        catch (Exception ex)
        {
            throw new SaveContainerException($"Could not read '{path}': {ex.Message}", ex);
        }

        ContainerText container = ContainerText.Load(bytes);

        if (!container.ContainsKey(SaveKey))
        {
            throw new SaveContainerException(
                $"'{path}' holds no save entry, so there is no campaign in it to edit.");
        }

        string stored = container.GetValue(SaveKey);

        if (!SaveChecksum.TryUnwrap(stored, out string payload, out bool checksumValid))
        {
            throw new SaveContainerException(
                $"The save entry in '{path}' carries no checksum, so this app will not rewrite it.");
        }

        if (!checksumValid)
        {
            throw new SaveContainerException(
                $"The save entry in '{path}' has a checksum the game will reject. Editing it would replace that " +
                "with a correct one and hide the damage, so it is left alone.");
        }

        return new SaveEditSession(path, Hashing.ComputeSha256(bytes), container, payload);
    }

    public string GetRecordBody(CampaignRecordRef campaign) => RecordAt(campaign).Body();

    /// <summary>In stored order, with repeats numbered.</summary>
    public IReadOnlyList<RawField> EnumerateFields(CampaignRecordRef campaign)
    {
        string body = GetRecordBody(campaign);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var fields = new List<RawField>();

        foreach (FieldSpan span in Fields.Locate(body))
        {
            counts.TryGetValue(span.Key, out int seen);
            counts[span.Key] = seen + 1;

            fields.Add(new RawField(
                span.Key,
                span.IsFlag ? null : body.Substring(span.ValueStart, span.ValueLength),
                seen,
                span.IsFlag));
        }

        return fields;
    }

    public string? GetFieldValue(CampaignRecordRef campaign, string key, int occurrence = 0)
        => Fields.GetValue(GetRecordBody(campaign), key, occurrence);

    public bool HasField(CampaignRecordRef campaign, string key)
        => Fields.Find(GetRecordBody(campaign), key) is not null;

    /// <summary>Adds the field when the campaign does not carry it yet.</summary>
    public void SetField(CampaignRecordRef campaign, string key, string value, int occurrence = 0)
    {
        string body = GetRecordBody(campaign);
        string? before = Fields.GetValue(body, key, occurrence);

        if (before is not null && string.Equals(before, value, StringComparison.Ordinal))
        {
            return;
        }

        Record(campaign, PartKey(campaign, key, occurrence), key, before, value);
        Apply(campaign, Fields.SetValue(body, key, value, occurrence));
    }

    /// <summary>Records what moved inside the field rather than the field as a whole, because
    /// DEATHPERSISTENTSAVEDATA carries karma, the echoes and every gate in one value.</summary>
    /// <param name="before">How the part read before, or null when it was not there.</param>
    /// <param name="after">How it reads now, or null when it has gone.</param>
    public void SetFieldPart(
        CampaignRecordRef campaign,
        string key,
        string value,
        string partName,
        string? before,
        string? after)
    {
        string body = GetRecordBody(campaign);

        if (string.Equals(Fields.GetValue(body, key), value, StringComparison.Ordinal))
        {
            return;
        }

        Record(campaign, PartKey(campaign, key, 0) + "|" + partName, partName, before, after);
        Apply(campaign, Fields.SetValue(body, key, value));
    }

    public void SetFlag(CampaignRecordRef campaign, string key, bool present)
    {
        string body = GetRecordBody(campaign);
        bool before = Fields.Find(body, key) is not null;

        if (before == present)
        {
            return;
        }

        Record(campaign, PartKey(campaign, key, 0), key, before ? "on" : "off", present ? "on" : "off");
        Apply(campaign, Fields.SetFlag(body, key, present));
    }

    public void RemoveField(CampaignRecordRef campaign, string key, int occurrence = 0)
    {
        string body = GetRecordBody(campaign);
        if (Fields.Find(body, key, occurrence) is not { } found)
        {
            return;
        }

        string before = found.IsFlag ? "on" : Fields.GetValue(body, key, occurrence) ?? "";

        Record(campaign, PartKey(campaign, key, occurrence), key, before, null);
        Apply(campaign, Fields.Remove(body, key, occurrence));
    }

    /// <summary>DEVOURMENTSTATE is written once per swallowed thing, so addressing those by position
    /// is the only way to change one of them.</summary>
    public void ReplaceFieldOccurrence(CampaignRecordRef campaign, string key, int occurrence, string value)
        => SetField(campaign, key, value, occurrence);

    public void InsertFieldAfter(CampaignRecordRef campaign, string key, int occurrence, string newField)
    {
        string added = Fields.KeyOf(newField);

        Record(campaign, PartKey(campaign, added, occurrence) + "|added", added, null, "added");
        Apply(campaign, Fields.InsertAfter(GetRecordBody(campaign), key, occurrence, newField));
    }

    public void ReplaceRecordBody(CampaignRecordRef campaign, string newBody, string description)
    {
        Record(campaign, PartKey(campaign, description, 0) + "|body", description, "", "changed");
        Apply(campaign, newBody);
    }

    /// <summary>Logs the change in the caller's own words.</summary>
    /// <param name="changeKey">Two edits sharing a key read as one line, so doing the same thing
    /// again replaces the line rather than adding one.</param>
    public void ReplaceRecordBody(CampaignRecordRef campaign, string newBody, string changeKey, string note)
    {
        if (_changes.TryGetValue(changeKey, out TrackedChange? existing))
        {
            existing.Note = note;
        }
        else
        {
            _changes[changeKey] = new TrackedChange(_changeOrder++, Name(campaign), note);
        }

        Apply(campaign, newBody);
    }

    /// <summary>In stored order.</summary>
    public IReadOnlyList<string> CampaignSlugcats => CampaignSplicer.Campaigns(_payload);

    public CampaignSlice? TakeCampaign(string slugcatId) => CampaignSplicer.Extract(_payload, slugcatId);

    /// <summary>Replaces whatever campaign the slot has for that slugcat. A whole-record change, so
    /// the plan built afterwards checks the result by what moved rather than by position.</summary>
    public CampaignSpliceReport PutCampaignIn(CampaignSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        string newPayload = CampaignSplicer.InsertCampaign(_payload, slice, out CampaignSpliceReport report);

        string what = report.Outcome == CampaignSpliceOutcome.Replaced
            ? "replaced this campaign"
            : "added this campaign to the slot";

        ApplySplice(newPayload, report, "campaign|" + slice.SlugcatId, Name(slice.SlugcatId), what);
        return report;
    }

    /// <param name="includeMaps">Whether the slugcat's map discovery goes with it. WipeSaveState
    /// leaves it behind, so a delete in place should too, but a move to another slot should take
    /// it.</param>
    public CampaignSpliceReport TakeCampaignOut(string slugcatId, bool includeMaps)
    {
        string newPayload = CampaignSplicer.RemoveCampaign(_payload, slugcatId, includeMaps, out CampaignSpliceReport report);

        if (report.Outcome == CampaignSpliceOutcome.NotFound && report.MapsRemoved == 0)
        {
            return report;
        }

        string what = includeMaps && report.MapsRemoved > 0
            ? "took this campaign out, map and all"
            : "took this campaign out";

        ApplySplice(newPayload, report, "campaign|" + slugcatId, Name(slugcatId), what);
        return report;
    }

    /// <summary>The game's own WipeAll also resets MISCPROG, clearing discovered shelters, ending
    /// ids, tutorial flags and campaign timers. This app cannot follow it there without rebuilding
    /// that record and dropping every field of it this app does not model, so MISCPROG is left
    /// exactly as it was found.</summary>
    public SlotDeleteReport DeleteCampaigns(SlotDeleteDepth depth)
    {
        IReadOnlyList<string> names = CampaignSlugcats
            .Select(Name)
            .ToArray();

        if (depth == SlotDeleteDepth.Everything)
        {
            return ClearEverything(names);
        }

        bool includeMaps = depth == SlotDeleteDepth.CampaignsAndMap;
        var removed = new List<string>();
        int maps = 0;

        // Read the list up front. Each removal rewrites the payload, so walking the live list while
        // taking things out of it would skip every second campaign.
        foreach (string slugcatId in CampaignSlugcats.ToArray())
        {
            CampaignSpliceReport report = TakeCampaignOut(slugcatId, includeMaps);

            if (report.Outcome == CampaignSpliceOutcome.Removed)
            {
                removed.Add(Name(slugcatId));
            }

            maps += report.MapsRemoved;
        }

        if (includeMaps)
        {
            // Every map left in the slot at this point belongs to nobody: the campaigns that owned
            // them have just gone, and the bare ones were never owned.
            string trimmed = CampaignSplicer.RemoveEveryMap(_payload, out IReadOnlyList<string> orphaned);

            if (orphaned.Count > 0)
            {
                _recordsRemoved.AddRange(orphaned);
                _splices++;
                _payload = trimmed;
                maps += orphaned.Count;

                // The write refuses a plan that describes no change, so taking maps has to say so in
                // its own right: a slot whose campaigns had already gone has nothing else to say.
                Note(MapKey, "This slot", orphaned.Count == 1
                    ? "took out the one region of map left in it"
                    : $"took out the {orphaned.Count} regions of map left in it");

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        return new SlotDeleteReport(removed, maps);
    }

    /// <summary>A slot emptied to a payload of no records is exactly what the game reads on a fresh
    /// install, where the value is the checksum and nothing after it. The copy the game keeps beside
    /// it in the same file goes too, or every campaign stays sitting in the file.</summary>
    private SlotDeleteReport ClearEverything(IReadOnlyList<string> names)
    {
        IReadOnlyList<string> before = _payload
            .Split(SavePayloadReader.RecordSeparator, StringSplitOptions.None);

        int maps = 0;
        int others = 0;

        foreach (string record in before)
        {
            if (record.Length == 0)
            {
                continue;
            }

            _recordsRemoved.Add(record);

            if (record.StartsWith("MAP", StringComparison.Ordinal))
            {
                maps++;
            }
            else if (!record.StartsWith(SaveStateHeader, StringComparison.Ordinal))
            {
                others++;
            }
        }

        _payload = "";
        _splices++;

        // Only worth clearing when it still holds something. An already empty one would otherwise
        // make a second delete look like it had work to do.
        bool clearedTheCopy = _container.ContainsKey(GameBackupKey)
            && _container.GetValue(GameBackupKey).Length > 0;
        if (clearedTheCopy)
        {
            _entriesToClear.Add(GameBackupKey);
        }

        Note(EmptiedKey, "This slot", names.Count == 0
            ? "emptied this slot out entirely"
            : "emptied this slot out entirely, campaigns and all");

        Changed?.Invoke(this, EventArgs.Empty);

        return new SlotDeleteReport(names, maps, others, clearedTheCopy);
    }

    /// <summary>The result is checked before anything is written.</summary>
    public SaveWritePlan BuildWritePlan(SizePolicy policy = SizePolicy.GrowIfNeeded)
        => SaveWritePlan.Build(
            this,
            _container,
            _originalPayload,
            _payload,
            _touchedRecords,
            Changes,
            _splices > 0 ? new RecordSetChange(_recordsWritten, _recordsRemoved) : null,
            _entriesToClear,
            policy);

    /// <summary>The record lists build up across splices rather than being replaced, so taking one
    /// campaign out and putting another in is still one set the plan can check the result against.</summary>
    private void ApplySplice(
        string newPayload,
        CampaignSpliceReport report,
        string changeKey,
        string campaignName,
        string note)
    {
        if (_changes.TryGetValue(changeKey, out TrackedChange? existing))
        {
            existing.Note = note;
        }
        else
        {
            _changes[changeKey] = new TrackedChange(_changeOrder++, campaignName, note);
        }

        _recordsWritten.AddRange(report.RecordsWritten);
        _recordsRemoved.AddRange(report.RecordsRemoved);
        _splices++;

        _payload = newPayload;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(CampaignRecordRef campaign, string newBody)
    {
        RecordSpan record = RecordAt(campaign);

        _payload = string.Concat(
            _payload.AsSpan(0, record.BodyStart),
            newBody,
            _payload.AsSpan(record.BodyStart + record.BodyLength));

        _touchedRecords.Add(campaign.RecordIndex);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folds a repeat of the same thing into the entry already there: the first write
    /// records where it started and every later one only moves the end. A value typed back to where
    /// it began stops being a change at all.</summary>
    /// <param name="before">What the record held, or null when it held nothing.</param>
    /// <param name="after">What it holds now, or null when the field has gone.</param>
    private void Record(CampaignRecordRef campaign, string changeKey, string label, string? before, string? after)
    {
        if (_changes.TryGetValue(changeKey, out TrackedChange? existing))
        {
            if (string.Equals(existing.Before, after, StringComparison.Ordinal))
            {
                _changes.Remove(changeKey);
                return;
            }

            existing.After = after;
            return;
        }

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        _changes[changeKey] = new TrackedChange(_changeOrder++, Name(campaign), label, before, after);
    }

    /// <summary>Replaces an earlier note under the same key rather than adding a second line.</summary>
    private void Note(string changeKey, string subject, string note)
    {
        if (_changes.TryGetValue(changeKey, out TrackedChange? existing))
        {
            existing.Note = note;
            return;
        }

        _changes[changeKey] = new TrackedChange(_changeOrder++, subject, note);
    }

    private static string PartKey(CampaignRecordRef campaign, string key, int occurrence)
        => campaign.RecordIndex.ToString(CultureInfo.InvariantCulture) + "|" + key + "|"
            + occurrence.ToString(CultureInfo.InvariantCulture);

    /// <summary>A null Before or After means the record did not carry the field then, kept as a null
    /// rather than a word because any word picked to stand for it is one a real field could hold.</summary>
    private sealed class TrackedChange
    {
        public TrackedChange(int order, string campaign, string label, string? before, string? after)
        {
            Order = order;
            Campaign = campaign;
            Label = label;
            Before = before;
            After = after;
        }

        /// <summary>For a change the caller has already put into words.</summary>
        public TrackedChange(int order, string campaign, string note)
            : this(order, campaign, "", null, null)
            => Note = note;

        /// <summary>The caller's own wording, used in place of one built from the value.</summary>
        public string? Note { get; set; }

        public int Order { get; }

        public string Campaign { get; }

        public string Label { get; }

        public string? Before { get; }

        public string? After { get; set; }

        public string Describe() => Note is not null
            ? $"{Campaign}: {Note}"
            : Before is null
                ? $"{Campaign}: set {Label} to {After}"
                : After is null
                    ? $"{Campaign}: removed {Label}"
                    : $"{Campaign}: {Label} {Before} to {After}";
    }

    private RecordSpan RecordAt(CampaignRecordRef campaign)
    {
        int index = 0;

        foreach (RecordSpan record in SavePayloadReader.EnumerateRecords(_payload))
        {
            if (index == campaign.RecordIndex)
            {
                if (!record.HeaderIs(SaveStateHeader))
                {
                    throw new SaveContainerException(
                        $"Record {campaign.RecordIndex} is not a campaign, so it cannot be edited as one.");
                }

                return record;
            }

            index++;
        }

        throw new SaveContainerException($"This save has no record {campaign.RecordIndex} to edit.");
    }

    private static string Name(CampaignRecordRef campaign) => Name(campaign.SlugcatId);

    private static string Name(string slugcatId) => SlugcatCatalog.ForId(slugcatId).DisplayName;

    private string Describe(CampaignRecordRef campaign, string key, string? before, string after)
        => before is null
            ? $"{Name(campaign)}: set {key} to {after}"
            : $"{Name(campaign)}: {key} {before} to {after}";

    /// <summary>Shares read and write access, so a file the game is holding open still opens.</summary>
    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
