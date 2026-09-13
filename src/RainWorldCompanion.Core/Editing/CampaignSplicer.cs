// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

/// <summary>The records are whole records, header included, and never a parsed model: a campaign
/// carries fields from mods, and the only way to move one without losing them is to move the text.</summary>
/// <param name="MapRecords">The MAP_ and MAPUPDATE_ records named after the slugcat, in stored order.
/// The bare MAP and MAPUPDATE records belong to no campaign and are never carried.</param>
public sealed record CampaignSlice(
    string SlugcatId,
    string SaveStateRecord,
    IReadOnlyList<string> MapRecords,
    IReadOnlyList<string>? DiscoveredShelters = null);

public enum CampaignSpliceOutcome
{
    NotFound,

    Added,

    Replaced,

    Removed,
}

/// <param name="MapsReplaced">Regions the target already had for this slugcat and now has again.</param>
/// <param name="MapsAdded">Regions the target did not have for this slugcat.</param>
/// <param name="MapsRemoved">Regions the target had for this slugcat and no longer has.</param>
/// <param name="RecordsWritten">A write plan holds the splice to exactly these: every other record
/// has to come back in the same order, character for character, or something was moved by accident.</param>
public sealed record CampaignSpliceReport(
    CampaignSpliceOutcome Outcome,
    int MapsReplaced,
    int MapsAdded,
    int MapsRemoved,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> RecordsWritten,
    IReadOnlyList<string> RecordsRemoved)
{
    public static CampaignSpliceReport Nothing { get; } = new(
        CampaignSpliceOutcome.NotFound,
        0,
        0,
        0,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    public bool DidNothing => Outcome == CampaignSpliceOutcome.NotFound
        && MapsReplaced == 0
        && MapsAdded == 0
        && MapsRemoved == 0;

    public int MapsCarried => MapsReplaced + MapsAdded;
}

/// <summary>
/// Moves one campaign between payloads by record surgery on the payload text. Records this app does
/// not know are copied across untouched. A campaign's identity is positional: the game reads the
/// value of the FIRST field of the body, whatever that field is called, so this class reads it the
/// same way rather than looking for SAV STATE NUMBER by name.
/// </summary>
public static class CampaignSplicer
{
    private const string SaveStateHeader = "SAVE STATE";
    private const string SlugcatField = "SAV STATE NUMBER";

    /// <summary>Header of a map record shared by every campaign in the slot.</summary>
    private const string SharedMapHeader = "MAP";

    private const string SharedMapUpdateHeader = "MAPUPDATE";

    private const string OwnedMapPrefix = "MAP_";

    private const string OwnedMapUpdatePrefix = "MAPUPDATE_";

    private const string MiscProgressHeader = "MISCPROG";

    private const string MiscFieldSeparator = "<mpdA>";

    private const string MiscValueSeparator = "<mpdB>";

    private const string MiscListSeparator = "<mpdC>";

    private const string ConditionalSheltersField = "CONDITIONALSHELTERDATA";

    private const string ShelterPartSeparator = " : ";

    /// <summary>The slugcat of every campaign in a payload, in stored order, repeats included.</summary>
    public static IReadOnlyList<string> Campaigns(string? payload)
    {
        var slugcats = new List<string>();

        foreach (string record in Split(payload))
        {
            if (IsSaveState(record) && SlugcatOf(record) is { Length: > 0 } slugcat)
            {
                slugcats.Add(slugcat);
            }
        }

        return slugcats;
    }

    public static bool Contains(string? payload, string slugcatId) => Extract(payload, slugcatId) is not null;

    /// <summary>Null when the payload holds no campaign for that slugcat. Map records for a slugcat
    /// with no campaign are not a campaign: WipeSaveState drops the SAVE STATE record and leaves the
    /// maps behind, so a real save can carry MAP_Inv records with no Inv campaign in it.</summary>
    public static CampaignSlice? Extract(string? payload, string slugcatId)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrWhiteSpace(slugcatId))
        {
            return null;
        }

        string? saveState = null;
        var maps = new List<string>();
        var shelters = new List<string>();

        foreach (string record in Split(payload))
        {
            if (IsSaveState(record))
            {
                if (saveState is null && Same(SlugcatOf(record), slugcatId))
                {
                    saveState = record;
                }

                continue;
            }

            if (Same(MapOwnerOf(record), slugcatId))
            {
                maps.Add(record);
            }

            if (string.Equals(HeaderOf(record), MiscProgressHeader, StringComparison.Ordinal))
            {
                foreach (string shelter in DiscoveredSheltersOf(record, slugcatId))
                {
                    if (!shelters.Contains(shelter, StringComparer.Ordinal))
                    {
                        shelters.Add(shelter);
                    }
                }
            }
        }

        return saveState is null ? null : new CampaignSlice(slugcatId, saveState, maps, shelters);
    }

    /// <summary>Map records are matched by slugcat and region, so one the target already has is
    /// replaced where it lies and the record order of the file it was written in survives.</summary>
    public static string InsertCampaign(string? payload, CampaignSlice slice, out CampaignSpliceReport report)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (ShelterDataProblem(slice) is { } problem)
        {
            throw new ArgumentException(problem, nameof(slice));
        }

        var log = new SpliceLog();
        WarnAboutWhatTheGameWillMakeOfIt(slice, log.Warnings);

        var slots = new List<string?>(Split(payload));
        var appended = new List<string>();

        MergeDiscoveredShelters(slots, slice, appended, log);

        CampaignSpliceOutcome outcome = ReplaceTheCampaign(slots, slice, appended, log);
        (int replaced, int added, int removed) = ReplaceTheMaps(slots, slice, appended, log);

        report = new CampaignSpliceReport(
            outcome, replaced, added, removed, log.Warnings, log.Written, log.Removed);

        return Rebuild(slots, appended);
    }

    public static string? ShelterDataProblem(CampaignSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.DiscoveredShelters is null)
        {
            return null;
        }

        if (!SafeSlugcatId(slice.SlugcatId))
        {
            return "The campaign slugcat id cannot be written into discovered shelter data.";
        }

        if (slice.DiscoveredShelters.Count > 4096)
        {
            return "The campaign carries more discovered shelters than a Rain World save can safely hold.";
        }

        foreach (string shelter in slice.DiscoveredShelters)
        {
            if (!SafeRoomId(shelter))
            {
                return "The campaign carries a discovered shelter whose room id is invalid.";
            }
        }

        return null;
    }

    private static bool SafeSlugcatId(string? value)
        => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Contains('<', StringComparison.Ordinal)
        && !value.Contains('>', StringComparison.Ordinal)
        && !value.Contains(ShelterPartSeparator, StringComparison.Ordinal)
        && value.All(character => !char.IsControl(character));

    private static bool SafeRoomId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character != '_' && !char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> DiscoveredSheltersOf(string record, string slugcatId)
    {
        string body = BodyOf(record);
        var shelters = new List<string>();

        foreach (string field in body.Split(MiscFieldSeparator, StringSplitOptions.None))
        {
            if (!field.StartsWith(ConditionalSheltersField + MiscValueSeparator, StringComparison.Ordinal))
            {
                continue;
            }

            string value = field[(ConditionalSheltersField.Length + MiscValueSeparator.Length)..];
            foreach (string entry in value.Split(MiscListSeparator, StringSplitOptions.None))
            {
                string[] parts = entry.Split(ShelterPartSeparator, StringSplitOptions.None);
                if (parts.Length > 1
                    && parts[0].Length > 0
                    && parts.Skip(1).Any(part => Same(part, slugcatId))
                    && !shelters.Contains(parts[0], StringComparer.Ordinal))
                {
                    shelters.Add(parts[0]);
                }
            }
        }

        return shelters;
    }

    private static void MergeDiscoveredShelters(
        List<string?> slots,
        CampaignSlice slice,
        List<string> appended,
        SpliceLog log)
    {
        if (slice.DiscoveredShelters is not { Count: > 0 })
        {
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            if (string.Equals(HeaderOf(slots[i]), MiscProgressHeader, StringComparison.Ordinal))
            {
                string before = slots[i]!;
                string after = MergeMiscProgress(before, slice.SlugcatId, slice.DiscoveredShelters);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    slots[i] = after;
                    log.Removed.Add(before);
                    log.Written.Add(after);
                }
                return;
            }
        }

        string entries = string.Join(
            MiscListSeparator,
            slice.DiscoveredShelters.Select(shelter => ShelterEntry(shelter, slice.SlugcatId)))
            + MiscListSeparator;
        string added =
            MiscProgressHeader + SavePayloadReader.HeaderSeparator
            + ConditionalSheltersField + MiscValueSeparator + entries + MiscFieldSeparator;
        appended.Add(added);
        log.Written.Add(added);
    }

    private static string MergeMiscProgress(
        string record,
        string slugcatId,
        IReadOnlyList<string> discoveredShelters)
    {
        string[] fields = BodyOf(record).Split(MiscFieldSeparator, StringSplitOptions.None);
        int conditional = Array.FindIndex(fields, field =>
            field.StartsWith(ConditionalSheltersField + MiscValueSeparator, StringComparison.Ordinal));

        if (conditional < 0)
        {
            var added = ConditionalSheltersField + MiscValueSeparator
                + string.Join(MiscListSeparator, discoveredShelters.Select(shelter => ShelterEntry(shelter, slugcatId)))
                + MiscListSeparator;
            var expanded = fields.ToList();
            expanded.Insert(EndsInEmpty(expanded) ? expanded.Count - 1 : expanded.Count, added);
            return MiscProgressHeader + SavePayloadReader.HeaderSeparator
                + string.Join(MiscFieldSeparator, expanded);
        }

        string prefix = ConditionalSheltersField + MiscValueSeparator;
        var entries = fields[conditional][prefix.Length..]
            .Split(MiscListSeparator, StringSplitOptions.None)
            .ToList();

        foreach (string shelter in discoveredShelters)
        {
            int found = entries.FindIndex(entry => ShelterName(entry) == shelter);
            if (found < 0)
            {
                entries.Insert(EndsInEmpty(entries) ? entries.Count - 1 : entries.Count, ShelterEntry(shelter, slugcatId));
            }
            else if (!ShelterMembers(entries[found]).Any(member => Same(member, slugcatId)))
            {
                entries[found] = AddShelterMember(entries[found], slugcatId);
            }
        }

        fields[conditional] = prefix + string.Join(MiscListSeparator, entries);
        return MiscProgressHeader + SavePayloadReader.HeaderSeparator
            + string.Join(MiscFieldSeparator, fields);
    }

    private static string ShelterName(string entry)
        => entry.Split(ShelterPartSeparator, StringSplitOptions.None).FirstOrDefault() ?? "";

    private static IEnumerable<string> ShelterMembers(string entry)
        => entry.Split(ShelterPartSeparator, StringSplitOptions.None).Skip(1).Where(part => part.Length > 0);

    private static string ShelterEntry(string shelter, string slugcatId)
        => shelter + ShelterPartSeparator + slugcatId + ShelterPartSeparator;

    private static string AddShelterMember(string entry, string slugcatId)
        => entry.EndsWith(ShelterPartSeparator, StringComparison.Ordinal)
            ? entry[..^ShelterPartSeparator.Length] + ShelterPartSeparator + slugcatId + ShelterPartSeparator
            : entry + ShelterPartSeparator + slugcatId + ShelterPartSeparator;

    private static bool EndsInEmpty(IReadOnlyList<string> values)
        => values.Count > 0 && values[^1].Length == 0;

    private static string BodyOf(string record)
    {
        int header = record.IndexOf(SavePayloadReader.HeaderSeparator, StringComparison.Ordinal);
        return header < 0 ? "" : record[(header + SavePayloadReader.HeaderSeparator.Length)..];
    }

    /// <param name="includeMaps">Whether the slugcat's map discovery goes with it. WipeSaveState
    /// leaves it behind, so a delete in place should too, but a move to another slot should take
    /// it.</param>
    public static string RemoveCampaign(
        string? payload,
        string slugcatId,
        bool includeMaps,
        out CampaignSpliceReport report)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrWhiteSpace(slugcatId))
        {
            report = CampaignSpliceReport.Nothing;
            return payload ?? "";
        }

        var slots = new List<string?>(Split(payload));
        var log = new SpliceLog();
        bool found = false;
        int mapsRemoved = 0;

        for (int i = 0; i < slots.Count; i++)
        {
            string record = slots[i]!;

            if (IsSaveState(record))
            {
                if (Same(SlugcatOf(record), slugcatId))
                {
                    slots[i] = null;
                    log.Removed.Add(record);
                    found = true;
                }

                continue;
            }

            if (includeMaps && Same(MapOwnerOf(record), slugcatId))
            {
                slots[i] = null;
                log.Removed.Add(record);
                mapsRemoved++;
            }
        }

        report = new CampaignSpliceReport(
            found ? CampaignSpliceOutcome.Removed : CampaignSpliceOutcome.NotFound,
            0,
            0,
            mapsRemoved,
            Array.Empty<string>(),
            Array.Empty<string>(),
            log.Removed);

        return Rebuild(slots, Array.Empty<string>());
    }

    /// <summary>Takes out every map record, the bare slot-shared ones included. For emptying a slot,
    /// where with every campaign gone every map in it belongs to nobody.</summary>
    public static string RemoveEveryMap(string? payload, out IReadOnlyList<string> removed)
    {
        if (string.IsNullOrEmpty(payload))
        {
            removed = Array.Empty<string>();
            return payload ?? "";
        }

        var slots = new List<string?>(Split(payload));
        var gone = new List<string>();

        for (int i = 0; i < slots.Count; i++)
        {
            if (IsAnyMap(slots[i]))
            {
                gone.Add(slots[i]!);
                slots[i] = null;
            }
        }

        removed = gone;
        return Rebuild(slots, Array.Empty<string>());
    }

    /// <summary>True for a map record of either kind, one a slugcat owns or one the slot shares.</summary>
    public static bool IsAnyMap(string? record) => IsSharedMap(record) || MapOwnerOf(record) is not null;

    /// <summary>Read the way the game reads it: the value of the first field of the body, whatever
    /// that field is called.</summary>
    public static string? SlugcatOf(string? record)
    {
        if (string.IsNullOrEmpty(record))
        {
            return null;
        }

        string first = FirstField(record);
        int value = first.IndexOf(SavePayloadReader.ValueSeparator, StringComparison.Ordinal);

        return value < 0 ? null : first[(value + SavePayloadReader.ValueSeparator.Length)..];
    }

    /// <summary>Null for a map record shared by the whole slot. The game writes per-slugcat records
    /// only with modded regions on, and a real save can hold both kinds at once.</summary>
    public static string? MapOwnerOf(string? record)
    {
        string header = HeaderOf(record);

        if (header.Length > OwnedMapUpdatePrefix.Length
            && header.StartsWith(OwnedMapUpdatePrefix, StringComparison.Ordinal))
        {
            return header[OwnedMapUpdatePrefix.Length..];
        }

        if (header.Length > OwnedMapPrefix.Length
            && header.StartsWith(OwnedMapPrefix, StringComparison.Ordinal))
        {
            return header[OwnedMapPrefix.Length..];
        }

        return null;
    }

    public static bool IsSharedMap(string? record)
    {
        string header = HeaderOf(record);

        return string.Equals(header, SharedMapHeader, StringComparison.Ordinal)
            || string.Equals(header, SharedMapUpdateHeader, StringComparison.Ordinal);
    }

    private static CampaignSpliceOutcome ReplaceTheCampaign(
        List<string?> slots,
        CampaignSlice slice,
        List<string> appended,
        SpliceLog log)
    {
        int at = -1;

        for (int i = 0; i < slots.Count; i++)
        {
            if (!IsSaveState(slots[i]) || !Same(SlugcatOf(slots[i]), slice.SlugcatId))
            {
                continue;
            }

            log.Removed.Add(slots[i]!);

            if (at < 0)
            {
                at = i;
                slots[i] = slice.SaveStateRecord;
                log.Written.Add(slice.SaveStateRecord);
                continue;
            }

            // The game keeps whichever it reaches first and rewrites the rest as they are.
            slots[i] = null;
            log.Warnings.Add(
                $"The slot held more than one {Name(slice.SlugcatId)} campaign. The extra one has been dropped.");
        }

        if (at >= 0)
        {
            return CampaignSpliceOutcome.Replaced;
        }

        appended.Add(slice.SaveStateRecord);
        log.Written.Add(slice.SaveStateRecord);
        return CampaignSpliceOutcome.Added;
    }

    private static (int Replaced, int Added, int Removed) ReplaceTheMaps(
        List<string?> slots,
        CampaignSlice slice,
        List<string> appended,
        SpliceLog log)
    {
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        var spare = new List<int>();

        for (int i = 0; i < slots.Count; i++)
        {
            if (!Same(MapOwnerOf(slots[i]), slice.SlugcatId))
            {
                continue;
            }

            if (!owned.TryAdd(MapKey(slots[i]!), i))
            {
                spare.Add(i);
            }
        }

        var taken = new HashSet<int>();
        int replaced = 0;
        int added = 0;

        foreach (string record in slice.MapRecords)
        {
            if (owned.TryGetValue(MapKey(record), out int at) && taken.Add(at))
            {
                log.Removed.Add(slots[at]!);
                slots[at] = record;
                log.Written.Add(record);
                replaced++;
                continue;
            }

            appended.Add(record);
            log.Written.Add(record);
            added++;
        }

        int removed = 0;

        foreach (int at in owned.Values.Concat(spare))
        {
            if (taken.Contains(at) || slots[at] is null)
            {
                continue;
            }

            log.Removed.Add(slots[at]!);
            slots[at] = null;
            removed++;
        }

        return (replaced, added, removed);
    }

    /// <summary>SaveToDisk writes a separator after every record it keeps, so a payload splits with
    /// an empty record on the end. New records go before that one.</summary>
    private static string Rebuild(List<string?> slots, IReadOnlyList<string> appended)
    {
        var kept = new List<string>(slots.Count + appended.Count);

        foreach (string? slot in slots)
        {
            if (slot is not null)
            {
                kept.Add(slot);
            }
        }

        int at = kept.Count > 0 && kept[^1].Length == 0 ? kept.Count - 1 : kept.Count;
        kept.InsertRange(at, appended);

        return string.Join(SavePayloadReader.RecordSeparator, kept);
    }

    private static void WarnAboutWhatTheGameWillMakeOfIt(CampaignSlice slice, List<string> warnings)
    {
        string header = HeaderOf(slice.SaveStateRecord);

        if (!string.Equals(header, SaveStateHeader, StringComparison.Ordinal))
        {
            warnings.Add(
                $"This is a '{header}' record rather than a campaign, so the game will not read it as one.");
            return;
        }

        string body = slice.SaveStateRecord[(header.Length + SavePayloadReader.HeaderSeparator.Length)..];

        // PlayerProgression.IsThereASavedGame only counts a record that splits into exactly two on
        // <progDivB>, so a body carrying one is a campaign the slot menu will not offer.
        if (body.Contains(SavePayloadReader.HeaderSeparator, StringComparison.Ordinal))
        {
            warnings.Add(
                "This campaign holds a <progDivB> separator, which the game splits records on, so the slot "
                + "will not offer it to play.");
        }

        string first = FirstField(slice.SaveStateRecord);
        int value = first.IndexOf(SavePayloadReader.ValueSeparator, StringComparison.Ordinal);

        if (value < 0)
        {
            warnings.Add(
                "The game reads which slugcat a campaign belongs to from the first value in it, and this one "
                + "has no value at all. Loading the slot would fail.");
            return;
        }

        string key = first[..value];

        if (!string.Equals(key, SlugcatField, StringComparison.Ordinal))
        {
            warnings.Add(
                $"The game takes the slugcat from the first field of a campaign whatever it is called, and this "
                + $"one is called '{key}'.");
        }

        string stored = first[(value + SavePayloadReader.ValueSeparator.Length)..];

        if (!Same(stored, slice.SlugcatId))
        {
            warnings.Add(
                $"This was stored as {Name(slice.SlugcatId)} but the campaign itself says {Name(stored)}, "
                + $"which is the one the game will read.");
        }
    }

    private static string FirstField(string record)
    {
        int header = record.IndexOf(SavePayloadReader.HeaderSeparator, StringComparison.Ordinal);
        if (header < 0)
        {
            return "";
        }

        string body = record[(header + SavePayloadReader.HeaderSeparator.Length)..];

        // Cut at <progDivB> before <svA>, because the game splits the record on <progDivB> first and
        // only ever sees what came before the second one.
        int next = body.IndexOf(SavePayloadReader.HeaderSeparator, StringComparison.Ordinal);
        if (next >= 0)
        {
            body = body[..next];
        }

        int field = body.IndexOf(SavePayloadReader.FieldSeparator, StringComparison.Ordinal);
        return field < 0 ? body : body[..field];
    }

    /// <summary>Slugcat and region together, which is what one map record is about.</summary>
    private static string MapKey(string record)
    {
        string header = HeaderOf(record);
        string body = record[Math.Min(record.Length, header.Length + SavePayloadReader.HeaderSeparator.Length)..];

        int next = body.IndexOf(SavePayloadReader.HeaderSeparator, StringComparison.Ordinal);
        string region = next < 0 ? body : body[..next];

        return header + "\n" + region;
    }

    private static string HeaderOf(string? record)
    {
        if (string.IsNullOrEmpty(record))
        {
            return "";
        }

        int split = record.IndexOf(SavePayloadReader.HeaderSeparator, StringComparison.Ordinal);
        return split < 0 ? record : record[..split];
    }

    private static bool IsSaveState(string? record)
        => string.Equals(HeaderOf(record), SaveStateHeader, StringComparison.Ordinal);

    private static string[] Split(string? payload)
        => string.IsNullOrEmpty(payload)
            ? new[] { "" }
            : payload.Split(SavePayloadReader.RecordSeparator, StringSplitOptions.None);

    private static bool Same(string? left, string? right)
        => left is not null
        && right is not null
        && string.Equals(Normalise(left), Normalise(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>A save written before the ids were names stores a number. Above 3 the game looks it
    /// up in a list built at runtime from the mods loaded, so this leaves it as the number.</summary>
    private static string Normalise(string id)
    {
        string trimmed = id.Trim();

        if (!int.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out int number))
        {
            return trimmed;
        }

        return number switch
        {
            0 => "White",
            1 => "Yellow",
            2 => "Red",
            3 => "Night",
            _ => trimmed,
        };
    }

    private static string Name(string? slugcatId) => SlugcatCatalog.ForId(slugcatId).DisplayName;

    /// <summary>A record replaced in place goes into both Written and Removed, which is what makes
    /// putting a campaign back where it came from cancel out to no change at all.</summary>
    private sealed class SpliceLog
    {
        public List<string> Written { get; } = new();

        public List<string> Removed { get; } = new();

        public List<string> Warnings { get; } = new();
    }
}
