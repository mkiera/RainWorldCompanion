// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Core.Saves;

/// <summary>Everything is optional: a value the blob does not carry stays null, and a collection it
/// does not carry stays empty.</summary>
/// <param name="RedsDeathStored">True when the blob carries a bare REDSDEATH token, which SaveToString
/// writes whenever the save is written as a death or a quit, whatever the flag holds. See
/// <see cref="RedsIllness.EffectiveRedsDeath"/>.</param>
public sealed record DeathPersistentData(
    int? Karma,
    int? KarmaCap,
    int? ReinforcedKarma,
    bool HasTheMark,
    bool Ascended,
    bool RedsDeathStored,
    bool RedExtraCycles,
    int? Deaths,
    int? Survives,
    int? Quits,
    IReadOnlyList<EchoRecord> Echoes,
    IReadOnlyList<string> UnlockedGates,
    IReadOnlyList<PassageRecord> Passages)
{
    /// <summary>An empty blob, and the result of reading one that could not be understood.</summary>
    public static DeathPersistentData Empty { get; } = new(
        null, null, null,
        false, false, false, false,
        null, null, null,
        Array.Empty<EchoRecord>(),
        Array.Empty<string>(),
        Array.Empty<PassageRecord>());
}

/// <summary>
/// The blob is the same shape as a record body one level down: fields split on &lt;dpA&gt;, each
/// either KEY&lt;dpB&gt;VALUE or a bare flag. Values carry their own angle bracket delimiters, so
/// only the first &lt;dpB&gt; in a field is a key boundary. A field this does not recognise leaves
/// its property null rather than failing the whole blob.
/// </summary>
public static class DeathPersistentReader
{
    public const string FieldSeparator = "<dpA>";

    /// <summary>Separates a field key from its value.</summary>
    public const string ValueSeparator = "<dpB>";

    /// <summary>Separates the entries of a list-valued field such as UNLOCKEDGATES.</summary>
    public const string ListSeparator = "<dpC>";

    /// <summary>Separates the entries of WINSTATE.</summary>
    public const string PassageSeparator = "<wsA>";

    /// <summary>Separates the parts of one WINSTATE entry.</summary>
    public const string PassagePartSeparator = "<egA>";

    private const string KarmaField = "KARMA";
    private const string KarmaCapField = "KARMACAP";
    private const string ReinforcedKarmaField = "REINFORCEDKARMA";
    private const string HasTheMarkField = "HASTHEMARK";
    private const string AscendedField = "ASCENDED";
    private const string RedsDeathField = "REDSDEATH";
    private const string RedExtraCyclesField = "REDEXTRACYCLES";
    private const string DeathsField = "DEATHS";
    private const string SurvivesField = "SURVIVES";
    private const string QuitsField = "QUITS";
    private const string GhostsField = "GHOSTS";
    private const string UnlockedGatesField = "UNLOCKEDGATES";
    private const string WinStateField = "WINSTATE";

    /// <summary>Never throws. A null, empty or unrecognised blob comes back as <see cref="DeathPersistentData.Empty"/>.</summary>
    public static DeathPersistentData Read(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return DeathPersistentData.Empty;
        }

        int? karma = null;
        int? karmaCap = null;
        int? reinforcedKarma = null;
        bool hasTheMark = false;
        bool ascended = false;
        bool redsDeath = false;
        bool redExtraCycles = false;
        int? deaths = null;
        int? survives = null;
        int? quits = null;
        IReadOnlyList<EchoRecord> echoes = Array.Empty<EchoRecord>();
        IReadOnlyList<string> gates = Array.Empty<string>();
        IReadOnlyList<PassageRecord> passages = Array.Empty<PassageRecord>();

        foreach (string field in value.Split(FieldSeparator, StringSplitOptions.None))
        {
            if (field.Length == 0)
            {
                continue;
            }

            int split = field.IndexOf(ValueSeparator, StringComparison.Ordinal);
            string key = split < 0 ? field : field.Substring(0, split);
            string? fieldValue = split < 0 ? null : field.Substring(split + ValueSeparator.Length);

            switch (key)
            {
                case KarmaField:
                    karma = ParseInt(fieldValue) ?? karma;
                    break;

                case KarmaCapField:
                    karmaCap = ParseInt(fieldValue) ?? karmaCap;
                    break;

                case ReinforcedKarmaField:
                    reinforcedKarma = ParseInt(fieldValue) ?? reinforcedKarma;
                    break;

                case HasTheMarkField:
                    hasTheMark = true;
                    break;

                case AscendedField:
                    ascended = true;
                    break;

                case RedsDeathField:
                    redsDeath = true;
                    break;

                case RedExtraCyclesField:
                    redExtraCycles = true;
                    break;

                case DeathsField:
                    deaths = ParseInt(fieldValue) ?? deaths;
                    break;

                case SurvivesField:
                    survives = ParseInt(fieldValue) ?? survives;
                    break;

                case QuitsField:
                    quits = ParseInt(fieldValue) ?? quits;
                    break;

                case GhostsField:
                    echoes = ParseGhosts(fieldValue);
                    break;

                case UnlockedGatesField:
                    gates = ParseUnlockedGates(fieldValue);
                    break;

                case WinStateField:
                    passages = ParseWinState(fieldValue);
                    break;
            }
        }

        return new DeathPersistentData(
            karma,
            karmaCap,
            reinforcedKarma,
            hasTheMark,
            ascended,
            redsDeath,
            redExtraCycles,
            deaths,
            survives,
            quits,
            echoes,
            gates,
            passages);
    }

    /// <summary>A comma separated list of "region:state" such as "SH:1,UW:2", where 1 is a hunch and
    /// 2 is an echo the player has spoken to. An entry with no colon is dropped.</summary>
    public static IReadOnlyList<EchoRecord> ParseGhosts(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<EchoRecord>();
        }

        var echoes = new List<EchoRecord>();

        foreach (string entry in value.Split(',', StringSplitOptions.None))
        {
            int colon = entry.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string region = entry.Substring(0, colon).Trim();
            if (region.Length == 0)
            {
                continue;
            }

            int? state = ParseInt(entry.Substring(colon + 1));
            if (state is null)
            {
                continue;
            }

            echoes.Add(new EchoRecord(region, state.Value));
        }

        return echoes.Count == 0 ? Array.Empty<EchoRecord>() : echoes;
    }

    /// <summary>Reads UNLOCKEDGATES, gate names separated by &lt;dpC&gt;.</summary>
    public static IReadOnlyList<string> ParseUnlockedGates(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<string>();
        }

        var gates = new List<string>();

        foreach (string entry in value.Split(ListSeparator, StringSplitOptions.None))
        {
            string gate = entry.Trim();
            if (gate.Length != 0)
            {
                gates.Add(gate);
            }
        }

        return gates.Count == 0 ? Array.Empty<string>() : gates;
    }

    /// <summary>Entries separated by &lt;wsA&gt;, each split on &lt;egA&gt; into a passage name, the
    /// consumed flag as 1 or 0, and a tracker. The tracker takes several shapes ("17", "30.29",
    /// "1.1.1."), so the raw text goes on <see cref="PassageRecord.Progress"/> untouched.</summary>
    public static IReadOnlyList<PassageRecord> ParseWinState(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<PassageRecord>();
        }

        var passages = new List<PassageRecord>();

        foreach (string entry in value.Split(PassageSeparator, StringSplitOptions.None))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            string[] parts = entry.Split(PassagePartSeparator, StringSplitOptions.None);
            if (parts.Length < 2)
            {
                continue;
            }

            string name = parts[0].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            bool consumed = string.Equals(parts[1].Trim(), "1", StringComparison.Ordinal);
            string progress = parts.Length > 2 ? parts[2].Trim() : "";

            passages.Add(new PassageRecord(name, consumed) { Progress = progress });
        }

        return passages.Count == 0 ? Array.Empty<PassageRecord>() : passages;
    }

    private static int? ParseInt(string? text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
}
