// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// Built only when meadow.json is present. A file that is there and could not be read keeps the
/// section and shows the reason, because that is a state the user can do something about.
/// </summary>
public sealed class MeadowProfileViewModel
{
    public MeadowProfileViewModel(MeadowProfile profile)
    {
        HasError = profile.ParseError is not null;
        ErrorText = profile.ParseError is { } error
            ? "meadow.json is there but could not be read: " + error
            : "";

        Stats = BuildStats(profile);

        Characters = profile.Characters
            .Select(character => new MeadowCharacterViewModel(character, profile.CurrentlySelectedCharacter))
            .ToList();
    }

    /// <summary>True when the file is there and the reader could not make sense of it.</summary>
    public bool HasError { get; }

    public string ErrorText { get; }

    public bool HasProfile => !HasError;

    public IReadOnlyList<StatTile> Stats { get; }

    public IReadOnlyList<MeadowCharacterViewModel> Characters { get; }

    public bool HasNoCharacters => Characters.Count == 0;

    private static IReadOnlyList<StatTile> BuildStats(MeadowProfile profile)
    {
        bool selected = profile.CurrentlySelectedCharacter.Length > 0;

        return new List<StatTile>
        {
            new(
                "Selected character",
                selected ? profile.CurrentlySelectedCharacter : CampaignViewModel.Missing,
                !selected),
            new(
                "Characters played",
                Number(profile.Characters.Count),
                false,
                "Rain Meadow writes an entry the first time a character is played, so a character that has never been picked is not in the file."),
            new(
                "Play time",
                FormatPlayTime(profile.TotalTimePlayed),
                false,
                "Counted across every character in the file."),
            new(
                "Next character",
                Progress(profile.CharacterUnlockProgress, MeadowProfile.CharacterUnlockThreshold),
                false,
                "Collectibles gathered towards unlocking another playable character."),
        };
    }

    /// <summary>"3 / 8", the way Rain Meadow counts a threshold.</summary>
    internal static string Progress(int have, int need) =>
        Number(have) + " / " + Number(need);

    /// <summary>
    /// The stored number is milliseconds: Rain Meadow adds 1000 / framesPerSecond to it once per
    /// game update, so a second of play adds 1000.
    /// </summary>
    internal static string FormatPlayTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        // A long. MeadowProfile caps an absurd stored number at what TimeSpan can hold, which is
        // 2.5 trillion hours, and casting that to int wraps to a negative number.
        if (time.TotalHours >= 1)
        {
            return Number((long)time.TotalHours) + "h " + Number(time.Minutes) + "m";
        }

        if (time.TotalMinutes >= 1)
        {
            return Number(time.Minutes) + "m " + Number(time.Seconds) + "s";
        }

        return Number(time.Seconds) + "s";
    }

    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One entry of meadow.json's characterProgress.</summary>
public sealed class MeadowCharacterViewModel
{
    public MeadowCharacterViewModel(MeadowCharacterProgress character, string selectedCharacter)
    {
        Name = character.Name.Length > 0 ? character.Name : "Unknown character";

        IsSelected = character.Name.Length > 0
            && string.Equals(character.Name, selectedCharacter, StringComparison.Ordinal);

        // The names are ExtEnum values, so a mod can put anything in these lists. Shown as stored.
        Emotes = character.UnlockedEmotes.Select(emote => new ChipTile(emote, "")).ToList();
        Skins = character.UnlockedSkins.Select(skin => new ChipTile(skin, "")).ToList();

        EmoteCountText = Emotes.Count == 1 ? "1 EMOTE" : MeadowProfileViewModel.Number(Emotes.Count) + " EMOTES";
        SkinCountText = Skins.Count == 1 ? "1 SKIN" : MeadowProfileViewModel.Number(Skins.Count) + " SKINS";

        Stats = BuildStats(character);
    }

    public string Name { get; }

    public bool IsSelected { get; }

    public IReadOnlyList<StatTile> Stats { get; }

    public IReadOnlyList<ChipTile> Emotes { get; }

    public IReadOnlyList<ChipTile> Skins { get; }

    public bool HasEmotes => Emotes.Count > 0;

    public bool HasNoEmotes => Emotes.Count == 0;

    public bool HasSkins => Skins.Count > 0;

    public bool HasNoSkins => Skins.Count == 0;

    public string EmoteCountText { get; }

    public string SkinCountText { get; }

    private static IReadOnlyList<StatTile> BuildStats(MeadowCharacterProgress character)
    {
        bool hasSkin = character.SelectedSkin.Length > 0;
        string room = character.SaveRoom;

        return new List<StatTile>
        {
            new(
                "Selected skin",
                hasSkin ? character.SelectedSkin : CampaignViewModel.Missing,
                !hasSkin),
            new(
                "Play time",
                MeadowProfileViewModel.FormatPlayTime(character.PlayTime),
                false),
            new(
                "Next emote",
                MeadowProfileViewModel.Progress(character.EmoteUnlockProgress, MeadowProfile.EmoteUnlockThreshold),
                false,
                "Collectibles gathered towards the next emote."),
            new(
                "Next skin",
                MeadowProfileViewModel.Progress(character.SkinUnlockProgress, MeadowProfile.SkinUnlockThreshold),
                false,
                "Collectibles gathered towards the next skin."),
            new(
                "Last seen",
                room.Length > 0 ? room : CampaignViewModel.Missing,
                room.Length == 0,
                room.Length > 0
                    ? "Stored coordinate: " + character.SaveLocation
                    : "The file records no room this character can be placed in."),
        };
    }
}
