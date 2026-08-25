// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Core.Editing;

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
    private readonly List<string> _changes = new();
    private readonly HashSet<int> _touchedRecords = new();

    private string _payload;

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

    /// <summary>One line per edit, in the order they were made, for a confirmation and a backup note.</summary>
    public IReadOnlyList<string> Changes => _changes;

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

        Apply(campaign, Fields.SetValue(body, key, value, occurrence), Describe(campaign, key, before, value));
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

        Apply(
            campaign,
            Fields.SetFlag(body, key, present),
            $"{Name(campaign)}: {(present ? "set" : "cleared")} {key}");
    }

    public void RemoveField(CampaignRecordRef campaign, string key, int occurrence = 0)
    {
        string body = GetRecordBody(campaign);
        if (Fields.Find(body, key, occurrence) is null)
        {
            return;
        }

        Apply(campaign, Fields.Remove(body, key, occurrence), $"{Name(campaign)}: removed {key}");
    }

    /// <summary>
    /// Replaces the value of one occurrence of a repeated key. DEVOURMENTSTATE is written once per
    /// swallowed thing, so addressing those by position is the only way to change one of them.
    /// </summary>
    public void ReplaceFieldOccurrence(CampaignRecordRef campaign, string key, int occurrence, string value)
        => SetField(campaign, key, value, occurrence);

    public void InsertFieldAfter(CampaignRecordRef campaign, string key, int occurrence, string newField)
        => Apply(
            campaign,
            Fields.InsertAfter(GetRecordBody(campaign), key, occurrence, newField),
            $"{Name(campaign)}: added {Fields.KeyOf(newField)}");

    /// <summary>Replaces a whole campaign record body, for an edit too large to express field by field.</summary>
    public void ReplaceRecordBody(CampaignRecordRef campaign, string newBody, string description)
        => Apply(campaign, newBody, description);

    /// <summary>
    /// Everything needed to write this session to disk, with the result checked before anything
    /// is written. A plan with problems is one to report, not one to write.
    /// </summary>
    public SaveWritePlan BuildWritePlan(SizePolicy policy = SizePolicy.GrowIfNeeded)
        => SaveWritePlan.Build(this, _container, _originalPayload, _payload, _touchedRecords, _changes, policy);

    private void Apply(CampaignRecordRef campaign, string newBody, string description)
    {
        RecordSpan record = RecordAt(campaign);

        _payload = string.Concat(
            _payload.AsSpan(0, record.BodyStart),
            newBody,
            _payload.AsSpan(record.BodyStart + record.BodyLength));

        _touchedRecords.Add(campaign.RecordIndex);
        _changes.Add(description);
        Changed?.Invoke(this, EventArgs.Empty);
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

    private static string Name(CampaignRecordRef campaign) => SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName;

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
