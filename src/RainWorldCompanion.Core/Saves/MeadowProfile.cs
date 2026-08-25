// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// What meadow.json says, extracted best effort. The file is Rain Meadow's own progression
/// store: RainMeadow.MeadowProgression.SaveLocation combines the persistent data path with
/// "meadow.json", and SaveProgression writes MeadowProgression.ProgressionData through
/// Newtonsoft JsonConvert with default settings, so every public field and property of that
/// class is a key here under its exact declared name.
///
/// Every field other than <see cref="ParseError"/> may be a partial result.
/// </summary>
public sealed class MeadowProfile
{
    private readonly IReadOnlyList<MeadowCharacterProgress> _characters = Array.Empty<MeadowCharacterProgress>();

    /// <summary>The name Rain Meadow gives the file, in the same folder as sav and online_sav.</summary>
    public const string FileName = "meadow.json";

    /// <summary>
    /// Collectibles a character has to gather before the next emote unlocks. From
    /// MeadowProgression's static constructor, which sets emoteProgressTreshold to 4.
    /// </summary>
    public const int EmoteUnlockThreshold = 4;

    /// <summary>Same for skins: skinProgressTreshold is 6.</summary>
    public const int SkinUnlockThreshold = 6;

    /// <summary>Same for characters: characterProgressTreshold is 8.</summary>
    public const int CharacterUnlockThreshold = 8;

    /// <summary>Non-null means the file could not be read and the other fields are empty.</summary>
    public string? ParseError { get; init; }

    /// <summary>
    /// Reads one meadow.json. Never throws: a missing, empty or malformed file comes back with
    /// <see cref="ParseError"/> set, because this runs while listing a folder the user did not
    /// curate and a mod update can change the file's shape at any time.
    /// </summary>
    public static MeadowProfile Read(string filePath) => MeadowProfileReader.Read(filePath);

    /// <summary>
    /// Reads <see cref="FileName"/> out of a save folder, which is either the live folder or a
    /// snapshot. Never throws.
    /// </summary>
    public static MeadowProfile ReadFromFolder(string folderPath) => MeadowProfileReader.ReadFromFolder(folderPath);

    /// <summary>Reads the json text itself. Never throws.</summary>
    public static MeadowProfile Parse(string? json) => MeadowProfileReader.Parse(json);

    /// <summary>Whether players collide with each other, the "collisionOn" key.</summary>
    public bool CollisionOn { get; init; }

    /// <summary>Whether player names are drawn over the creatures, the "displayNames" key.</summary>
    public bool DisplayNames { get; init; }

    /// <summary>
    /// Collectibles gathered towards the next playable character, counted against
    /// <see cref="CharacterUnlockThreshold"/>.
    /// </summary>
    public int CharacterUnlockProgress { get; init; }

    /// <summary>
    /// The character the menu is set to, for example "Slugcat". This is a
    /// MeadowProgression.Character ExtEnum value, so a mod can add names this app has never seen.
    /// Empty when the key was absent.
    /// </summary>
    public string CurrentlySelectedCharacter { get; init; } = "";

    /// <summary>
    /// One entry per character the player has progress on, sorted by name. Never null. The
    /// underlying "characterProgress" object is keyed by character name and only holds the
    /// characters that have been played, so a fresh profile has none.
    /// </summary>
    public IReadOnlyList<MeadowCharacterProgress> Characters
    {
        get => _characters;
        init => _characters = value ?? Array.Empty<MeadowCharacterProgress>();
    }

    /// <summary>
    /// The entry for <see cref="CurrentlySelectedCharacter"/>, or null when there is none. Rain Meadow
    /// creates the entry the first time a character is played, so a selected character with no
    /// entry is normal.
    /// </summary>
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

    /// <summary>
    /// The largest millisecond count that converts to a TimeSpan. A tick is 100ns, TimeSpan holds
    /// a long of them, so anything past this cannot be represented and
    /// TimeSpan.FromMilliseconds throws on it.
    /// </summary>
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

    /// <summary>
    /// A stored millisecond count as a duration, bounded to what TimeSpan can hold.
    ///
    /// The number comes out of a json file a mod owns and a user can edit, so it is not trusted to
    /// be a plausible play time. This class documents itself as never throwing, and it is read on
    /// the dispatcher with nothing catching underneath, so an absurd stored number has to render as
    /// a large duration rather than take the window down.
    /// </summary>
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

        // Counted as a long. A capped duration is 2.5 trillion hours, and casting that to int
        // wraps to a negative number rather than overflowing loudly.
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

/// <summary>
/// One entry of meadow.json's "characterProgress" object: what a single character has unlocked
/// and where it left off. Mirrors MeadowProgression.ProgressionData.CharacterProgressionData.
/// </summary>
public sealed class MeadowCharacterProgress
{
    private readonly IReadOnlyList<string> _unlockedEmotes = Array.Empty<string>();
    private readonly IReadOnlyList<string> _unlockedSkins = Array.Empty<string>();
    private readonly IReadOnlyList<string> _emoteHotbar = Array.Empty<string>();

    /// <summary>Marks a room name that Rain World could not resolve. See <see cref="SaveRoom"/>.</summary>
    private const string InvalidCoordinatePrefix = "INV.";

    /// <summary>The key this entry sat under, for example "Slugcat". Empty only if the key was.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The stored "timePlayed" number, in milliseconds.
    ///
    /// The unit comes from RainMeadow.RainMeadow.RainWorldGame_Update1, which runs once per game
    /// update and does timePlayed += 1000 / game.framesPerSecond. A second of updates therefore
    /// adds framesPerSecond * (1000 / framesPerSecond), which is 1000. The division is integer,
    /// so at a frame rate that does not divide 1000 the count runs slightly short of real time.
    /// </summary>
    public long PlayTimeMilliseconds { get; init; }

    /// <summary>
    /// <see cref="PlayTimeMilliseconds"/> as a duration, bounded to what TimeSpan can hold. See
    /// <see cref="MeadowProfile.ToPlayTime"/> for why the stored number is not trusted.
    /// </summary>
    public TimeSpan PlayTime => MeadowProfile.ToPlayTime(PlayTimeMilliseconds);

    /// <summary>
    /// Collectibles gathered towards the next emote, counted against
    /// <see cref="MeadowProfile.EmoteUnlockThreshold"/>.
    /// </summary>
    public int EmoteUnlockProgress { get; init; }

    /// <summary>
    /// Collectibles gathered towards the next skin, counted against
    /// <see cref="MeadowProfile.SkinUnlockThreshold"/>.
    /// </summary>
    public int SkinUnlockProgress { get; init; }

    /// <summary>
    /// Emote names this character has unlocked, for example "emoteHello". These are
    /// MeadowProgression.Emote ExtEnum values, so the list can hold names a mod added.
    /// </summary>
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

    /// <summary>
    /// Where this character last was, as stored: "room.x.y.node", for example "SU_A41.26.17.2".
    /// Rain Meadow writes it through WorldCoordinate.SaveToString via its WorldCoordinateConverter.
    /// Empty when the key was absent.
    /// </summary>
    public string SaveLocation { get; init; } = "";

    /// <summary>
    /// The room part of <see cref="SaveLocation"/>, for example "SU_A41". Empty when there is no
    /// location, or when the stored coordinate is one WorldCoordinate.SaveToString marked invalid:
    /// that form is "INV.name.x.y.node" and carries no resolved room.
    /// </summary>
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

    /// <summary>
    /// The tint as stored: six hex characters of RGB, for example "000000". Rain Meadow writes
    /// it through its UnityColorConverter. Empty when the key was absent.
    /// </summary>
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
