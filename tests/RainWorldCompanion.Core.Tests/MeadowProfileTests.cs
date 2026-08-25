using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// meadow.json is Rain Meadow's character progression: unlocked emotes and skins, the skin and
/// character currently picked, where the character last was, and how long it has been played.
/// The file is small and hand-editable, so the reader is fail-soft in the same way the save
/// container reader is: anything it cannot make sense of comes back with ParseError set instead
/// of throwing, because this runs while listing a folder the user did not curate.
///
/// The reference content is the whole file from one real install, quoted byte for byte.
/// </summary>
public class MeadowProfileTests
{
    [Fact]
    public void The_real_file_parses()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteText("meadow.json", MeadowJson.Live);

        var profile = MeadowProfile.Read(path);

        Assert.Null(profile.ParseError);
    }

    [Fact]
    public void The_real_file_reports_the_character_that_is_currently_selected()
    {
        var profile = ReadLive();

        Assert.Equal("Slugcat", profile.CurrentlySelectedCharacter);
    }

    [Fact]
    public void The_real_file_reports_its_one_character()
    {
        var profile = ReadLive();

        var character = Assert.Single(profile.Characters);
        Assert.Equal("Slugcat", character.Name);
        Assert.Equal("SU_A41.26.17.2", character.SaveLocation);
        Assert.True(character.EverSeenInMenu);
    }

    [Fact]
    public void The_real_file_reports_the_unlocked_emotes_in_the_order_they_are_stored()
    {
        var character = Assert.Single(ReadLive().Characters);

        Assert.Equal(
            new[] { "emoteHello", "emoteHappy", "emoteSad", "emoteConfused" },
            character.UnlockedEmotes.ToArray());
    }

    [Fact]
    public void The_real_file_reports_the_unlocked_skins_and_the_one_that_is_selected()
    {
        var character = Assert.Single(ReadLive().Characters);

        Assert.Equal(new[] { "Slugcat_Survivor" }, character.UnlockedSkins.ToArray());
        Assert.Equal("Slugcat_Survivor", character.SelectedSkin);
    }

    [Fact]
    public void The_real_file_reports_the_emote_hotbar_including_the_symbols()
    {
        var character = Assert.Single(ReadLive().Characters);

        Assert.Equal(
            new[]
            {
                "emoteHello", "emoteHappy", "emoteSad", "emoteConfused",
                "symbolYes", "symbolNo", "symbolQuestion", "symbolExclamation",
            },
            character.EmoteHotbar.ToArray());
    }

    [Fact]
    public void Time_played_is_milliseconds()
    {
        // MeadowCharacterSelectPage.GetPlaytime reads the same field as
        // TimeSpan.FromMilliseconds(timePlayed), so 147575 is two and a half minutes, not two
        // and a half days. The unit is the whole reason this field is converted rather than
        // shown raw.
        var character = Assert.Single(ReadLive().Characters);

        Assert.Equal(TimeSpan.FromMilliseconds(147575), character.PlayTime);
    }

    [Fact]
    public void The_real_file_reports_the_two_lobby_toggles()
    {
        var profile = ReadLive();

        Assert.False(profile.CollisionOn);
        Assert.False(profile.DisplayNames);
    }

    [Fact]
    public void Several_characters_all_come_through()
    {
        // The install this was taken from has one character. The schema holds one entry per
        // character the player has touched, so the reader has to carry all of them.
        const string Json = """
            {"collisionOn":true,"displayNames":true,"characterUnlockProgress":2,"characterProgress":
            {"Slugcat":{"timePlayed":1000,"unlockedSkins":["Slugcat_Survivor"],"selectedSkin":"Slugcat_Survivor"},
            "Lizard":{"timePlayed":2000,"unlockedSkins":["Lizard_Pink"],"selectedSkin":"Lizard_Pink"}},
            "currentlySelectedCharacter":"Lizard"}
            """;

        var profile = ReadJson(Json);

        Assert.Null(profile.ParseError);
        Assert.Equal(
            new[] { "Lizard", "Slugcat" },
            profile.Characters.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("Lizard", profile.CurrentlySelectedCharacter);
        Assert.True(profile.CollisionOn);
        Assert.True(profile.DisplayNames);
    }

    [Fact]
    public void A_character_with_nothing_unlocked_yet_reads_as_empty_rather_than_null()
    {
        const string Json = """
            {"characterProgress":{"Slugcat":{"timePlayed":0}},"currentlySelectedCharacter":"Slugcat"}
            """;

        var character = Assert.Single(ReadJson(Json).Characters);

        Assert.Empty(character.UnlockedEmotes);
        Assert.Empty(character.UnlockedSkins);
        Assert.Empty(character.EmoteHotbar);
        Assert.Equal(TimeSpan.Zero, character.PlayTime);
    }

    [Fact]
    public void Unknown_properties_are_ignored_rather_than_failing()
    {
        // A later version of the mod adds fields. An old copy of this app has to keep reading the
        // file, because the alternative is telling the player their profile is broken when it is
        // only newer.
        const string Json = """
            {"collisionOn":false,"displayNames":false,"characterUnlockProgress":0,
            "somethingAddedLater":{"nested":[1,2,3]},
            "characterProgress":{"Slugcat":{"timePlayed":5,"selectedSkin":"Slugcat_Monk",
            "unlockedSkins":["Slugcat_Monk"],"aFieldFromTheFuture":true}},
            "currentlySelectedCharacter":"Slugcat"}
            """;

        var profile = ReadJson(Json);

        Assert.Null(profile.ParseError);
        var character = Assert.Single(profile.Characters);
        Assert.Equal("Slugcat_Monk", character.SelectedSkin);
    }

    [Fact]
    public void A_missing_file_gives_an_error()
    {
        using var temp = new TempDirectory();

        var profile = MeadowProfile.Read(temp.Resolve("meadow.json"));

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void An_empty_file_gives_an_error()
    {
        var profile = ReadJson("");

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void A_file_holding_only_whitespace_gives_an_error()
    {
        var profile = ReadJson("   \r\n  ");

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void An_empty_json_object_gives_an_error()
    {
        // Well-formed JSON that says nothing. There is no character progression in it, so it is
        // not a profile, and reporting it as one would show the player an empty panel with no
        // explanation.
        var profile = ReadJson("{}");

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void Truncated_json_gives_an_error()
    {
        var profile = ReadJson(MeadowJson.Live.Substring(0, 120));

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void A_json_array_where_an_object_belongs_gives_an_error()
    {
        var profile = ReadJson("[{\"collisionOn\":false}]");

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void A_json_string_where_an_object_belongs_gives_an_error()
    {
        var profile = ReadJson("\"meadow\"");

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void Character_progress_of_the_wrong_shape_gives_an_error_rather_than_throwing()
    {
        var profile = ReadJson("{\"characterProgress\":[\"Slugcat\"],\"currentlySelectedCharacter\":\"Slugcat\"}");

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void Binary_content_gives_an_error_rather_than_throwing()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("meadow.json", SyntheticSave.GarbageBytes());

        var profile = MeadowProfile.Read(path);

        Assert.NotNull(profile.ParseError);
        Assert.Empty(profile.Characters);
    }

    [Fact]
    public void An_error_is_one_short_line_a_list_row_can_show()
    {
        var profile = ReadJson("{ this is not json");

        Assert.NotNull(profile.ParseError);
        Assert.False(profile.ParseError!.Contains('\n'), "the error runs onto a second line");
        Assert.False(profile.ParseError.Contains('\r'), "the error runs onto a second line");
        Assert.True(profile.ParseError.Length <= 200, $"the error is {profile.ParseError.Length} characters long");
    }

    [Fact]
    public void A_directory_where_the_file_belongs_gives_an_error_rather_than_throwing()
    {
        using var temp = new TempDirectory();
        temp.CreateSubdirectory("meadow.json");

        var profile = MeadowProfile.Read(temp.Resolve("meadow.json"));

        Assert.NotNull(profile.ParseError);
    }

    [Fact]
    public void Reading_never_writes_anything()
    {
        using var temp = new TempDirectory();
        temp.WriteText("meadow.json", MeadowJson.Live);
        var before = temp.ReadTree();

        MeadowProfile.Read(temp.Resolve("meadow.json"));

        SnapshotLayout.AssertTreeUnchanged(before, temp.ReadTree());
    }

    // ---- a stored play time the file has no business holding ----

    [Fact]
    public void A_play_time_past_what_a_duration_can_hold_renders_rather_than_throwing()
    {
        // timePlayed is a number in a json file a mod owns and a user can edit. Anything past
        // about 9.22e14 milliseconds cannot be a TimeSpan, and the panel reads this on the
        // dispatcher with nothing catching underneath it, so an absurd number has to come back as
        // a large duration rather than take the window down.
        var profile = ReadJson(WithTimePlayed("9000000000000000"));

        Assert.Null(profile.ParseError);
        var character = Assert.Single(profile.Characters);

        Assert.True(character.PlayTime > TimeSpan.FromDays(365), "the capped duration is not a large one");
        Assert.True(profile.TotalTimePlayed > TimeSpan.FromDays(365));
        Assert.NotEmpty(profile.Describe());
        Assert.NotEmpty(character.Describe());
    }

    [Fact]
    public void The_largest_stored_play_time_a_long_can_hold_still_renders()
    {
        var profile = ReadJson(WithTimePlayed("9223372036854775807"));

        Assert.Null(profile.ParseError);
        Assert.NotEmpty(profile.Describe());
    }

    [Fact]
    public void A_negative_stored_play_time_reads_as_no_time_at_all()
    {
        var profile = ReadJson(WithTimePlayed("-5000"));

        Assert.Null(profile.ParseError);
        Assert.Equal(TimeSpan.Zero, Assert.Single(profile.Characters).PlayTime);
        Assert.Equal(TimeSpan.Zero, profile.TotalTimePlayed);
    }

    [Fact]
    public void An_ordinary_play_time_is_unaffected_by_the_cap()
    {
        // 147575 ms is what the real file holds, which is 2 minutes and 27 seconds.
        var character = Assert.Single(ReadLive().Characters);

        Assert.Equal(147575, character.PlayTimeMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(147575), character.PlayTime);
    }

    /// <summary>The real file with one number swapped, so nothing else about it moves.</summary>
    private static string WithTimePlayed(string milliseconds)
        => MeadowJson.Live.Replace("\"timePlayed\":147575", "\"timePlayed\":" + milliseconds, StringComparison.Ordinal);

    private static MeadowProfile ReadLive() => ReadJson(MeadowJson.Live);

    private static MeadowProfile ReadJson(string json)
    {
        using var temp = new TempDirectory();
        return MeadowProfile.Read(temp.WriteText("meadow.json", json));
    }
}

/// <summary>
/// meadow.json as it sits in one real Rain World save folder, 574 bytes, quoted whole. Line
/// breaks were added to keep it readable and are insignificant to JSON.
/// </summary>
internal static class MeadowJson
{
    public const string Live = """
        {"collisionOn":false,"displayNames":false,"characterUnlockProgress":0,"characterProgress":
        {"Slugcat":{"timePlayed":147575,"emoteUnlockProgress":0,"skinUnlockProgress":0,
        "unlockedEmotes":["emoteHello","emoteHappy","emoteSad","emoteConfused"],
        "unlockedSkins":["Slugcat_Survivor"],"saveLocation":"SU_A41.26.17.2","everSeenInMenu":true,
        "selectedSkin":"Slugcat_Survivor","tintAmount":0.0,"tintColor":"000000",
        "emoteHotbar":["emoteHello","emoteHappy","emoteSad","emoteConfused","symbolYes","symbolNo",
        "symbolQuestion","symbolExclamation"]}},"currentlySelectedCharacter":"Slugcat"}
        """;
}
