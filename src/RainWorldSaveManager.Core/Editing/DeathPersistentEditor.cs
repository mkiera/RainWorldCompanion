// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Core.Editing;

/// <summary>
/// Edits the DEATHPERSISTENTSAVEDATA value of a campaign, which is where karma, the echoes the
/// player has met and the gates they have opened are kept.
///
/// Every function takes the current blob and returns a new one, so the caller writes it back with
/// a single field edit on the record. The blob has its own grammar one level below the record's,
/// and the same rule applies to it: fields this app does not recognise are carried through
/// untouched, because a blob rebuilt from what
/// <see cref="DeathPersistentReader"/> understands would lose the rest.
/// </summary>
public static class DeathPersistentEditor
{
    public const string KarmaField = "KARMA";
    public const string KarmaCapField = "KARMACAP";
    public const string ReinforcedKarmaField = "REINFORCEDKARMA";
    public const string HasTheMarkField = "HASTHEMARK";
    public const string AscendedField = "ASCENDED";
    public const string RedsDeathField = "REDSDEATH";
    public const string RedExtraCyclesField = "REDEXTRACYCLES";
    public const string DeathsField = "DEATHS";
    public const string SurvivesField = "SURVIVES";
    public const string QuitsField = "QUITS";
    public const string GhostsField = "GHOSTS";
    public const string UnlockedGatesField = "UNLOCKEDGATES";

    /// <summary>An echo the player has never come across.</summary>
    public const int EchoNeverSeen = 0;

    /// <summary>An echo the player has sensed but not spoken to.</summary>
    public const int EchoSensed = 1;

    /// <summary>An echo the player has spoken to.</summary>
    public const int EchoTalkedTo = 2;

    private static readonly DelimitedFields Fields = DelimitedFields.DeathPersistent;

    public static string SetInt(string? blob, string key, int value)
        => Fields.SetValue(blob ?? "", key, value.ToString(CultureInfo.InvariantCulture));

    public static string SetFlag(string? blob, string key, bool present) => Fields.SetFlag(blob ?? "", key, present);

    public static string Remove(string? blob, string key) => Fields.Remove(blob ?? "", key);

    public static string? GetValue(string? blob, string key) => Fields.GetValue(blob ?? "", key);

    /// <summary>
    /// Sets what the player knows about one echo. <see cref="EchoNeverSeen"/> takes the region out
    /// of the list, which is what a save that has never met that echo looks like.
    ///
    /// The order of the entries the blob already has is kept, because it is the order the game
    /// wrote them in and there is no reason for this app to have an opinion about it.
    /// </summary>
    public static string SetEcho(string? blob, string regionCode, int state)
    {
        string current = blob ?? "";

        if (string.IsNullOrWhiteSpace(regionCode))
        {
            return current;
        }

        string region = regionCode.Trim();
        var entries = new List<EchoRecord>(DeathPersistentReader.ParseGhosts(Fields.GetValue(current, GhostsField)));

        int existing = entries.FindIndex(e => string.Equals(e.RegionCode, region, StringComparison.OrdinalIgnoreCase));

        if (state == EchoNeverSeen)
        {
            if (existing < 0)
            {
                return current;
            }

            entries.RemoveAt(existing);
        }
        else if (existing >= 0)
        {
            entries[existing] = new EchoRecord(entries[existing].RegionCode, state);
        }
        else
        {
            entries.Add(new EchoRecord(region, state));
        }

        return SetEchoes(current, entries);
    }

    /// <summary>Replaces the whole echo list.</summary>
    public static string SetEchoes(string? blob, IReadOnlyList<EchoRecord> echoes)
    {
        if (echoes.Count == 0)
        {
            // A blob with an empty GHOSTS field is not what a save that has met no echoes looks
            // like. That save has no GHOSTS field at all.
            return Fields.Remove(blob ?? "", GhostsField);
        }

        string value = string.Join(
            ",",
            echoes.Select(e => e.RegionCode + ":" + e.State.ToString(CultureInfo.InvariantCulture)));

        return Fields.SetValue(blob ?? "", GhostsField, value);
    }

    /// <summary>Opens or closes one gate, keeping the order of the gates already listed.</summary>
    public static string SetGate(string? blob, string gateName, bool unlocked)
    {
        string current = blob ?? "";

        if (string.IsNullOrWhiteSpace(gateName))
        {
            return current;
        }

        string gate = gateName.Trim();
        var gates = new List<string>(DeathPersistentReader.ParseUnlockedGates(Fields.GetValue(current, UnlockedGatesField)));

        int existing = gates.FindIndex(g => string.Equals(g, gate, StringComparison.OrdinalIgnoreCase));

        if (unlocked)
        {
            if (existing >= 0)
            {
                return current;
            }

            gates.Add(gate);
        }
        else
        {
            if (existing < 0)
            {
                return current;
            }

            gates.RemoveAt(existing);
        }

        return SetGates(current, gates);
    }

    /// <summary>Replaces the whole unlocked gate list.</summary>
    public static string SetGates(string? blob, IReadOnlyList<string> gates)
    {
        var kept = gates.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList();

        if (kept.Count == 0)
        {
            return Fields.Remove(blob ?? "", UnlockedGatesField);
        }

        return Fields.SetValue(
            blob ?? "",
            UnlockedGatesField,
            string.Join(DeathPersistentReader.ListSeparator, kept));
    }
}
