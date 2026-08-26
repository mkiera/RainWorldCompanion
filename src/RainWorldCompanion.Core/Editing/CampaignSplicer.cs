// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

/// <summary>
/// One campaign lifted out of a payload, kept as the characters the file holds.
///
/// The records are whole records, header included, and never a parsed model. A campaign carries
/// fields from every mod that has ever touched it, and the only way to move one without losing
/// them is to move the text.
/// </summary>
/// <param name="MapRecords">
/// The map discovery this slugcat has, in stored order. These are the MAP_ and MAPUPDATE_ records
/// named after the slugcat. The bare MAP and MAPUPDATE records are shared by every campaign in the
/// slot, so they belong to no campaign and are never carried.
/// </param>
public sealed record CampaignSlice(
    string SlugcatId,
    string SaveStateRecord,
    IReadOnlyList<string> MapRecords);

/// <summary>What a splice did to the target payload.</summary>
public enum CampaignSpliceOutcome
{
    /// <summary>The target holds no campaign for that slugcat, so nothing was taken out.</summary>
    NotFound,

    /// <summary>The campaign was not in the target and has been added to it.</summary>
    Added,

    /// <summary>The target already had this slugcat, and that campaign is now this one.</summary>
    Replaced,

    /// <summary>The campaign has been taken out of the target.</summary>
    Removed,
}

/// <summary>What a splice did, and anything about it worth saying out loud.</summary>
/// <param name="MapsReplaced">Regions the target already had for this slugcat and now has again.</param>
/// <param name="MapsAdded">Regions the target did not have for this slugcat.</param>
/// <param name="MapsRemoved">
/// Regions the target had for this slugcat and no longer has, because the campaign arriving has not
/// been to them.
/// </param>
/// <param name="RecordsWritten">
/// The records the splice put into the payload, and <paramref name="RecordsRemoved"/> the ones it
/// took out. A write plan holds the splice to exactly these: every other record of the payload has
/// to come back in the same order, character for character, or something was moved by accident.
/// </param>
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

    /// <summary>True when the payload came back the same as it went in.</summary>
    public bool DidNothing => Outcome == CampaignSpliceOutcome.NotFound
        && MapsReplaced == 0
        && MapsAdded == 0
        && MapsRemoved == 0;

    public int MapsCarried => MapsReplaced + MapsAdded;
}

/// <summary>
/// Moves one campaign between payloads: takes it out of one, puts it into another, or drops it.
///
/// Every operation is record surgery on the payload text. Records this app does not know are copied
/// across untouched, and records it is not moving are left exactly where they were, so a payload
/// that goes out and comes back is the same characters it started as.
///
/// Two facts from PlayerProgression shape all of it:
///
/// The game finds a campaign by splitting a record on the first &lt;progDivB&gt; and handing what
/// follows to BackwardsCompatibilityRemix.ParseSaveNumber, which reads the value of the FIRST field
/// of the body, whatever that field is called. So a campaign's identity is positional, and this
/// class reads it the same way rather than looking for SAV STATE NUMBER by name. Every save seen so
/// far writes that field first, and a save that does not is warned about instead of guessed at.
///
/// PlayerProgression.SaveToDisk rewrites the payload record by record in stored order and appends a
/// campaign it did not find at the end, so that is where a new one goes here too. Order carries no
/// meaning to the game: a real save has MAP records both before and after its SAVE STATE.
/// </summary>
public static class CampaignSplicer
{
    private const string SaveStateHeader = "SAVE STATE";
    private const string SlugcatField = "SAV STATE NUMBER";

    /// <summary>Header of a map record shared by every campaign in the slot.</summary>
    private const string SharedMapHeader = "MAP";

    private const string SharedMapUpdateHeader = "MAPUPDATE";

    /// <summary>Header prefix of a map record belonging to one slugcat, as SaveToDisk writes it.</summary>
    private const string OwnedMapPrefix = "MAP_";

    private const string OwnedMapUpdatePrefix = "MAPUPDATE_";

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

    /// <summary>
    /// Takes one campaign out of a payload without changing it, or null when the payload holds no
    /// campaign for that slugcat.
    ///
    /// Map records for a slugcat with no campaign are not a campaign. A real save carries
    /// MAP_Inv records with no Inv campaign anywhere in it, which is what a slot looks like after
    /// the game has wiped a save state: WipeSaveState drops the SAVE STATE record and leaves the
    /// maps behind.
    /// </summary>
    public static CampaignSlice? Extract(string? payload, string slugcatId)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrWhiteSpace(slugcatId))
        {
            return null;
        }

        string? saveState = null;
        var maps = new List<string>();

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
        }

        return saveState is null ? null : new CampaignSlice(slugcatId, saveState, maps);
    }

    /// <summary>
    /// Puts a campaign into a payload, replacing the one already there for that slugcat.
    ///
    /// Map records are matched to the target's by slugcat and region, so one the target already has
    /// is replaced where it lies. That keeps the record order of the file it was written in, and it
    /// is what makes putting a campaign back where it came from give the same characters back.
    /// </summary>
    public static string InsertCampaign(string? payload, CampaignSlice slice, out CampaignSpliceReport report)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var log = new SpliceLog();
        WarnAboutWhatTheGameWillMakeOfIt(slice, log.Warnings);

        var slots = new List<string?>(Split(payload));
        var appended = new List<string>();

        CampaignSpliceOutcome outcome = ReplaceTheCampaign(slots, slice, appended, log);
        (int replaced, int added, int removed) = ReplaceTheMaps(slots, slice, appended, log);

        report = new CampaignSpliceReport(
            outcome, replaced, added, removed, log.Warnings, log.Written, log.Removed);

        return Rebuild(slots, appended);
    }

    /// <summary>
    /// Takes a campaign out of a payload.
    /// </summary>
    /// <param name="includeMaps">
    /// Whether the slugcat's map discovery goes with it. The game's own WipeSaveState leaves it
    /// behind, so a campaign deleted in place should leave it too. A campaign moved to another slot
    /// should take it, or the map it had stays in a slot that no longer has the campaign.
    /// </param>
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

    /// <summary>
    /// Takes out the map records the whole slot shares, which the game writes when modded regions
    /// are off.
    ///
    /// These belong to no campaign, so removing one campaign never touches them. Emptying a slot of
    /// every campaign is the case where that stops being true: a map shared by nothing is not shared
    /// at all, and leaving it is what makes a slot still report explored regions after everything
    /// that explored them has gone.
    /// </summary>
    public static string RemoveSharedMaps(string? payload, out IReadOnlyList<string> removed)
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
            if (IsSharedMap(slots[i]))
            {
                gone.Add(slots[i]!);
                slots[i] = null;
            }
        }

        removed = gone;
        return Rebuild(slots, Array.Empty<string>());
    }

    /// <summary>
    /// Which slugcat a campaign record belongs to, read the way the game reads it: the value of the
    /// first field of the body, whatever that field is called.
    /// </summary>
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

    /// <summary>
    /// Which slugcat a map record belongs to, or null for one shared by the whole slot.
    ///
    /// The game writes per-slugcat map records only with modded regions enabled, which is what
    /// Downpour and Watcher turn on. Without them it writes bare MAP records that every campaign in
    /// the slot reads, and a real save can hold both at once.
    /// </summary>
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

    /// <summary>True for a map record every campaign in the slot shares.</summary>
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

            // The game keeps whichever it reaches first and rewrites the rest as they are, so a
            // second copy would go on shadowing this one for as long as the file lives.
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

    /// <summary>
    /// Puts the records back together, appending the new ones where the game appends its own.
    ///
    /// SaveToDisk writes a separator after every record it keeps, so a payload ends with one and
    /// splits with an empty record on the end. Appending before that empty record is what keeps the
    /// payload ending the way the game writes it.
    /// </summary>
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

    /// <summary>
    /// Says what the game will do with this campaign that a person would not expect, without
    /// refusing to write any of it.
    /// </summary>
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

    /// <summary>The first field of a record body, which is where a campaign keeps its slugcat.</summary>
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

        // The separator is a character no header or region name holds, so no pair of them can
        // collide by running together.
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

    /// <summary>
    /// The name a numbered slugcat stands for.
    ///
    /// A save written before the ids were names stores a number, and
    /// BackwardsCompatibilityRemix.ParsePlayerNumber turns 0 to 3 into White, Yellow, Red and Night.
    /// Anything above that it looks up in a list the game builds at runtime from the mods loaded, so
    /// this app cannot resolve it and leaves it as the number it found.
    /// </summary>
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

    /// <summary>
    /// What a splice did, collected as it goes.
    ///
    /// The two record lists are what a write plan checks the result against. A record replaced in
    /// place is in both of them, which is what makes putting a campaign back where it came from
    /// cancel out to no change at all.
    /// </summary>
    private sealed class SpliceLog
    {
        public List<string> Written { get; } = new();

        public List<string> Removed { get; } = new();

        public List<string> Warnings { get; } = new();
    }
}
