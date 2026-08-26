// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// Rain Meadow writes MeadowProgression.ProgressionData through Newtonsoft JsonConvert with default
/// settings, so every public field and property of that class is a key here under its exact declared
/// name. Every field other than <see cref="ParseError"/> may be a partial result.
/// </summary>
public sealed class MeadowProfile
{
    private readonly IReadOnlyList<MeadowCharacterProgress> _characters = Array.Empty<MeadowCharacterProgress>();

    /// <summary>The name Rain Meadow gives the file, in the same folder as sav and online_sav.</summary>
    public const string FileName = "meadow.json";

    /// <summary>Collectibles a character gathers before the next emote unlocks, from
    /// MeadowProgression's emoteProgressTreshold.</summary>
    public const int EmoteUnlockThreshold = 4;

    /// <summary>Same for skins: skinProgressTreshold is 6.</summary>
    public const int SkinUnlockThreshold = 6;

    /// <summary>Same for characters: characterProgressTreshold is 8.</summary>
    public const int CharacterUnlockThreshold = 8;

    /// <summary>Non-null means the file could not be read and the other fields are empty.</summary>
    public string? ParseError { get; init; }

    /// <summary>Never throws: a missing, empty or malformed file comes back with
    /// <see cref="ParseError"/> set.</summary>
    public static MeadowProfile Read(string filePath) => MeadowProfileReader.Read(filePath);

    /// <summary>Never throws.</summary>
    public static MeadowProfile ReadFromFolder(string folderPath) => MeadowProfileReader.ReadFromFolder(folderPath);

    /// <summary>Reads the json text itself. Never throws.</summary>
    public static MeadowProfile Parse(string? json) => MeadowProfileReader.Parse(json);

    /// <summary>Whether players collide with each other, the "collisionOn" key.</summary>
    public bool CollisionOn { get; init; }

    /// <summary>Whether player names are drawn over the creatures, the "displayNames" key.</summary>
    public bool DisplayNames { get; init; }

    /// <summary>Counted against <see cref="CharacterUnlockThreshold"/>.</summary>
    public int CharacterUnlockProgress { get; init; }

    /// <summary>For example "Slugcat". An ExtEnum value, so a mod can add names this app has never
    /// seen. Empty when the key was absent.</summary>
    public string CurrentlySelectedCharacter { get; init; } = "";

    /// <summary>Sorted by name, never null. Only holds the characters that have been played, so a
    /// fresh profile has none.</summary>
    public IReadOnlyList<MeadowCharacterProgress> Characters
    {
        get => _characters;
        init => _characters = value ?? Array.Empty<MeadowCharacterProgress>();
    }

    /// <summary>Null when there is none. Rain Meadow creates the entry the first time a character is
    /// played, so a selected character with no entry is normal.</summary>
    public MeadowCharacterProgress? SelectedCharacterProgress
    {
        get
        {
            foreach (MeadowCharacterProgress character in Characters)
            {
                if (string.Equals(character.Name, CurrentlySelectedCharacter, StringComparison.Ordinal))
                {
                    return character;
                }
            }

            return null;
        }
    }

    /// <summary>The largest millisecond count that converts to a TimeSpan: past this,
    /// TimeSpan.FromMilliseconds throws.</summary>
    private const long MaxPlayTimeMilliseconds = long.MaxValue / TimeSpan.TicksPerMillisecond;

    /// <summary>Play time across every character. Saturates rather than overflowing.</summary>
    public TimeSpan TotalTimePlayed
    {
        get
        {
            long total = 0;
            foreach (MeadowCharacterProgress character in Characters)
            {
                long value = character.PlayTimeMilliseconds;
                if (value <= 0)
                {
                    continue;
                }

                if (value >= MaxPlayTimeMilliseconds - total)
                {
                    total = MaxPlayTimeMilliseconds;
                    break;
                }

                total += value;
            }

            return ToPlayTime(total);
        }
    }

    /// <summary>Bounded to what TimeSpan can hold. The number comes out of a json file a user can
    /// edit, and this is read on the dispatcher with nothing catching underneath, so an absurd
    /// stored number has to render as a large duration rather than take the window down.</summary>
    internal static TimeSpan ToPlayTime(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        long capped = Math.Min(milliseconds, MaxPlayTimeMilliseconds);
        return TimeSpan.FromTicks(capped * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>One line for the UI, for example "Rain Meadow: Slugcat, 1 character, 2m played".</summary>
    public string Describe()
    {
        const string Label = "Rain Meadow";

        if (ParseError is not null)
        {
            return Label + ": unreadable (" + ParseError + ")";
        }

        if (Characters.Count == 0)
        {
            return Label + ": no character progress";
        }

        var text = new StringBuilder(Label);
        text.Append(": ");
        text.Append(CurrentlySelectedCharacter.Length == 0 ? "no character selected" : CurrentlySelectedCharacter);
        text.Append(", ");
        text.Append(Characters.Count.ToString(CultureInfo.InvariantCulture));
        text.Append(Characters.Count == 1 ? " character, " : " characters, ");
        text.Append(FormatPlayTime(TotalTimePlayed));
        text.Append(" played");
        return text.ToString();
    }

    /// <summary>Coarse duration for a one line summary: "3h 12m", "7m" or "41s".</summary>
    internal static string FormatPlayTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        // Counted as a long: a capped duration is 2.5 trillion hours, and casting that to int wraps.
        if (time.TotalHours >= 1)
        {
            return ((long)time.TotalHours).ToString(CultureInfo.InvariantCulture) + "h "
                + time.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }

        if (time.TotalMinutes >= 1)
        {
            return time.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }

        return time.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
    }
}

/// <summary>Mirrors MeadowProgression.ProgressionData.CharacterProgressionData.</summary>
public sealed class MeadowCharacterProgress
{
    private readonly IReadOnlyList<string> _unlockedEmotes = Array.Empty<string>();
    private readonly IReadOnlyList<string> _unlockedSkins = Array.Empty<string>();
    private readonly IReadOnlyList<string> _emoteHotbar = Array.Empty<string>();

    /// <summary>Marks a room name that Rain World could not resolve. See <see cref="SaveRoom"/>.</summary>
    private const string InvalidCoordinatePrefix = "INV.";

    /// <summary>The key this entry sat under, for example "Slugcat". Empty only if the key was.</summary>
    public string Name { get; init; } = "";

    /// <summary>The stored "timePlayed" number, in milliseconds. Rain Meadow accumulates it as an
    /// integer 1000 / framesPerSecond per update, so at a frame rate that does not divide 1000 the
    /// count runs slightly short of real time.</summary>
    public long PlayTimeMilliseconds { get; init; }

    /// <summary>Bounded to what TimeSpan can hold. See <see cref="MeadowProfile.ToPlayTime"/>.</summary>
    public TimeSpan PlayTime => MeadowProfile.ToPlayTime(PlayTimeMilliseconds);

    /// <summary>Counted against <see cref="MeadowProfile.EmoteUnlockThreshold"/>.</summary>
    public int EmoteUnlockProgress { get; init; }

    /// <summary>Counted against <see cref="MeadowProfile.SkinUnlockThreshold"/>.</summary>
    public int SkinUnlockProgress { get; init; }

    /// <summary>For example "emoteHello". ExtEnum values, so the list can hold names a mod added.</summary>
    public IReadOnlyList<string> UnlockedEmotes
    {
        get => _unlockedEmotes;
        init => _unlockedEmotes = value ?? Array.Empty<string>();
    }

    /// <summary>Skin names this character has unlocked, for example "Slugcat_Survivor".</summary>
    public IReadOnlyList<string> UnlockedSkins
    {
        get => _unlockedSkins;
        init => _unlockedSkins = value ?? Array.Empty<string>();
    }

    /// <summary>"room.x.y.node", for example "SU_A41.26.17.2". Empty when the key was absent.</summary>
    public string SaveLocation { get; init; } = "";

    /// <summary>The room part, for example "SU_A41". Empty when there is no location, or when the
    /// stored coordinate is one WorldCoordinate.SaveToString marked invalid: that form is
    /// "INV.name.x.y.node" and carries no resolved room.</summary>
    public string SaveRoom
    {
        get
        {
            if (SaveLocation.Length == 0
                || SaveLocation.StartsWith(InvalidCoordinatePrefix, StringComparison.Ordinal))
            {
                return "";
            }

            int stop = SaveLocation.IndexOf('.');
            return stop < 0 ? SaveLocation : SaveLocation.Substring(0, stop);
        }
    }

    /// <summary>Whether the character has ever been looked at in the menu.</summary>
    public bool EverSeenInMenu { get; init; }

    /// <summary>The skin currently picked for this character, for example "Slugcat_Survivor".</summary>
    public string SelectedSkin { get; init; } = "";

    /// <summary>How strongly <see cref="TintColor"/> is applied, 0 to 1.</summary>
    public float TintAmount { get; init; }

    /// <summary>Six hex characters of RGB, for example "000000". Empty when the key was absent.</summary>
    public string TintColor { get; init; } = "";

    /// <summary>The emotes bound to the in-game wheel, in wheel order.</summary>
    public IReadOnlyList<string> EmoteHotbar
    {
        get => _emoteHotbar;
        init => _emoteHotbar = value ?? Array.Empty<string>();
    }

    /// <summary>One line for the UI, for example "Slugcat: 4 emotes, 1 skin, 2m played".</summary>
    public string Describe()
    {
        string label = Name.Length == 0 ? "Unknown character" : Name;

        var text = new StringBuilder(label);
        text.Append(": ");
        text.Append(UnlockedEmotes.Count.ToString(CultureInfo.InvariantCulture));
        text.Append(UnlockedEmotes.Count == 1 ? " emote, " : " emotes, ");
        text.Append(UnlockedSkins.Count.ToString(CultureInfo.InvariantCulture));
        text.Append(UnlockedSkins.Count == 1 ? " skin, " : " skins, ");
        text.Append(MeadowProfile.FormatPlayTime(PlayTime));
        text.Append(" played");
        return text.ToString();
    }
}
