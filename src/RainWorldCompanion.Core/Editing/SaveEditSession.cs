// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

/// <summary>
/// Which campaign inside a slot an edit applies to.
///
/// The index is what the session works from, because it stays put while a field is edited. The
/// slugcat id is what a person reads, and it is carried alongside so a caller does not have to go
/// back to the payload to say which campaign it changed.
/// </summary>
public sealed record CampaignRecordRef(int RecordIndex, string SlugcatId);

/// <summary>One field of a record body as the file stores it.</summary>
/// <param name="Occurrence">Which of the fields sharing this key it is, counting from zero.</param>
/// <param name="IsFlag">True when the field is a bare token that means true by being present.</param>
public sealed record RawField(string Key, string? Value, int Occurrence, bool IsFlag);

/// <summary>
/// One slot file open for editing, held as the payload it came from with edits applied by
/// substring surgery.
///
/// The rule this class exists to keep: a payload is never rebuilt from a parsed model. The game
/// itself keeps lists of save strings it did not recognise and writes them back untouched, which
/// is how a save that a mod has written still loads without that mod. Rebuilding from
/// <see cref="CampaignSummary"/> would drop every one of them, along with the map records, the
/// region states and everything else this app does not read. So each edit replaces the characters
/// of one field and leaves the rest of the file alone.
///
/// A session refuses to open a save whose digest is already wrong. Wrapping a fresh, correct
/// digest around a payload that failed its old one would turn a file the game rejects into one it
/// accepts, which hides the damage instead of reporting it.
/// </summary>
public sealed class SaveEditSession
{
    private const string SaveKey = "save";
    private const string SaveStateHeader = "SAVE STATE";
    private const string SlugcatField = "SAV STATE NUMBER";

    private static readonly DelimitedFields Fields = DelimitedFields.Record;

    private readonly ContainerText _container;
    private readonly string _originalPayload;
    private readonly Dictionary<string, TrackedChange> _changes = new(StringComparer.Ordinal);
    private readonly HashSet<int> _touchedRecords = new();

    /// <summary>Records a campaign splice put in, and took out, so the plan can check it did only that.</summary>
    private readonly List<string> _recordsWritten = new();

    private readonly List<string> _recordsRemoved = new();

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

    /// <summary>The file this session was opened from.</summary>
    public string FilePath { get; }

    /// <summary>
    /// SHA-256 of the file as it was when the session opened.
    ///
    /// This is what makes an edit safe to write later. The session holds a payload in memory while
    /// somebody fills in a dialog, and in that time the game or Steam Cloud can put different bytes
    /// on disk. The write refuses when the file no longer hashes to this, rather than overwriting
    /// whatever arrived with an edit of something else.
    /// </summary>
    public string FileSha256 { get; }

    /// <summary>The payload with every edit so far applied.</summary>
    public string Payload => _payload;

    public bool IsDirty => !string.Equals(_payload, _originalPayload, StringComparison.Ordinal);

    /// <summary>
    /// One line per thing changed, in the order it was first touched. A field edited over and over,
    /// which is what typing into a box does, reads as the one move it made.
    /// </summary>
    public IReadOnlyList<string> Changes => _changes.Values
        .OrderBy(change => change.Order)
        .Select(change => change.Describe())
        .ToArray();

    /// <summary>Raised after every edit, so a curated editor and the raw field list stay in step.</summary>
    public event EventHandler? Changed;

    /// <summary>The campaigns in this slot, in the order the payload stores them.</summary>
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

    /// <summary>The body of one campaign record as it currently stands.</summary>
    public string GetRecordBody(CampaignRecordRef campaign) => RecordAt(campaign).Body();

    /// <summary>Every field of a campaign record, in stored order, with repeats numbered.</summary>
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

    /// <summary>Sets a keyed field, adding it when the campaign does not carry it yet.</summary>
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

    /// <summary>
    /// Writes a field that holds values of its own, recording what moved inside it rather than the
    /// field as a whole.
    ///
    /// DEATHPERSISTENTSAVEDATA is one field carrying karma, the echoes and every gate, so a log
    /// keyed on the field name would collapse all of them into one line reading as an unbroken wall
    /// of delimiters. The caller names the part it changed and how it reads.
    /// </summary>
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

    /// <summary>Adds or removes a bare flag such as HASTHEGLOW.</summary>
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

    /// <summary>
    /// Replaces the value of one occurrence of a repeated key. DEVOURMENTSTATE is written once per
    /// swallowed thing, so addressing those by position is the only way to change one of them.
    /// </summary>
    public void ReplaceFieldOccurrence(CampaignRecordRef campaign, string key, int occurrence, string value)
        => SetField(campaign, key, value, occurrence);

    public void InsertFieldAfter(CampaignRecordRef campaign, string key, int occurrence, string newField)
    {
        string added = Fields.KeyOf(newField);

        Record(campaign, PartKey(campaign, added, occurrence) + "|added", added, null, "added");
        Apply(campaign, Fields.InsertAfter(GetRecordBody(campaign), key, occurrence, newField));
    }

    /// <summary>Replaces a whole campaign record body, for an edit too large to express field by field.</summary>
    public void ReplaceRecordBody(CampaignRecordRef campaign, string newBody, string description)
    {
        Record(campaign, PartKey(campaign, description, 0) + "|body", description, "", "changed");
        Apply(campaign, newBody);
    }

    /// <summary>
    /// Replaces a record body, logging the change in the caller's own words.
    ///
    /// An edit that spans several fields at once has no key and value to read a line out of, and
    /// "DEVOURMENTSTATE changed" says nothing a person can check. The caller knows it moved a row
    /// or renamed a status, so it writes that and this stores it.
    /// </summary>
    /// <param name="changeKey">
    /// What is being changed, so doing it again replaces the line rather than adding one. Two edits
    /// sharing a key are one line; two edits that are different things need different keys.
    /// </param>
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

    /// <summary>The campaign the session is holding for each slugcat, in stored order.</summary>
    public IReadOnlyList<string> CampaignSlugcats => CampaignSplicer.Campaigns(_payload);

    /// <summary>Takes one campaign out of this slot, to store it or to move it somewhere else.</summary>
    public CampaignSlice? TakeCampaign(string slugcatId) => CampaignSplicer.Extract(_payload, slugcatId);

    /// <summary>
    /// Puts a campaign into this slot, replacing whatever campaign the slot has for that slugcat.
    ///
    /// This is a whole-record change rather than a field edit, so the plan built afterwards checks
    /// the result a different way: it holds the splice to the records it said it wrote and took out,
    /// and every other record of the payload has to come back in the same order character for
    /// character.
    /// </summary>
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

    /// <summary>
    /// Takes a campaign out of this slot.
    /// </summary>
    /// <param name="includeMaps">
    /// Whether the slugcat's map discovery goes with it. The game's own WipeSaveState leaves it
    /// behind, so deleting a campaign in place should too. Moving one to another slot should take
    /// it, or the map stays in a slot that no longer has the campaign.
    /// </param>
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

    /// <summary>
    /// Everything needed to write this session to disk, with the result checked before anything
    /// is written. A plan with problems is one to report, not one to write.
    /// </summary>
    public SaveWritePlan BuildWritePlan(SizePolicy policy = SizePolicy.GrowIfNeeded)
        => SaveWritePlan.Build(
            this,
            _container,
            _originalPayload,
            _payload,
            _touchedRecords,
            Changes,
            _splices > 0 ? new RecordSetChange(_recordsWritten, _recordsRemoved) : null,
            policy);

    /// <summary>
    /// Replaces the whole payload after a campaign was moved in or out, and notes what moved.
    ///
    /// The record lists build up across splices rather than being replaced, so taking one campaign
    /// out and putting another in is still one set of records the plan can check the result against.
    /// </summary>
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

    /// <summary>
    /// Notes that one thing moved, folding a repeat of the same thing into the entry already there.
    ///
    /// This is what keeps a typed number from reading as one change per keystroke. Every character
    /// typed into a box writes the field again, and each of those writes is a real edit to the
    /// payload, but they are all the same field going from where it started to where it ended up.
    /// The first write records where it started and every later one only moves the end.
    ///
    /// A value typed back to where it began stops being a change at all, which is the same answer
    /// <see cref="IsDirty"/> gives for the payload as a whole.
    /// </summary>
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

    private static string PartKey(CampaignRecordRef campaign, string key, int occurrence)
        => campaign.RecordIndex.ToString(CultureInfo.InvariantCulture) + "|" + key + "|"
            + occurrence.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One thing that moved, and where it started, however many writes it took to get there.
    ///
    /// A null on either side means the record did not carry the field then. That is a state of its
    /// own rather than a value, so it is a null and not a chosen word: any word picked to stand for
    /// it is one a real field could also hold.
    /// </summary>
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

    /// <summary>
    /// Opens read-only and shares read and write access, the same way the reader does, so a save
    /// folder the game happens to be holding open still opens for reading.
    /// </summary>
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
