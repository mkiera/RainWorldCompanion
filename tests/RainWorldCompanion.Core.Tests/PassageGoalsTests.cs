using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A WINSTATE entry is a passage name, the tracker's <c>consumed</c> flag, and its progress. None
/// of the three is "earned".
///
/// WinState.EndgameTracker.ToString writes field two from <c>consumed</c>, which is set once the
/// player has used the passage to travel. Menu.EndgameTokens draws a passage token when
/// <c>GoalFullfilled &amp;&amp; !consumed</c>, and GoalFullfilled is the progress against the
/// maximum WinState.CreateAndAddTracker built the tracker with. So the flag on disk marks the
/// passages the game no longer offers, and the ones it does offer have to be worked out.
///
/// The strings below are the ones the live save holds, campaign by campaign.
/// </summary>
public class PassageGoalsTests
{
    // ---- The inversion this exists to stop ----

    /// <summary>
    /// Monk's WINSTATE holds "Martyr&lt;egA&gt;1&lt;egA&gt;1" and "Survivor&lt;egA&gt;0&lt;egA&gt;5".
    /// Reading field two as "earned" reports exactly the wrong one of the two.
    /// </summary>
    [Fact]
    public void A_spent_passage_and_an_available_one_are_told_apart()
    {
        var passages = DeathPersistentReader.ParseWinState(
            "Martyr<egA>1<egA>1<wsA>Survivor<egA>0<egA>5<wsA>");

        var martyr = Assert.Single(passages, p => p.Name == "Martyr");
        Assert.True(martyr.Consumed);
        Assert.Equal(true, martyr.Goal.Fulfilled);

        var survivor = Assert.Single(passages, p => p.Name == "Survivor");
        Assert.False(survivor.Consumed);
        Assert.Equal(true, survivor.Goal.Fulfilled);

        // Survivor is the one the game offers. Martyr is spent.
        Assert.True(survivor.Goal.Fulfilled == true && !survivor.Consumed);
        Assert.False(martyr.Goal.Fulfilled == true && !martyr.Consumed);
    }

    /// <summary>
    /// Spearmaster in the live save has four integer passages standing at their maximum with the
    /// consumed flag clear, and one dragon slayer already spent. Every one of the four is
    /// available in game and the spent one is not.
    /// </summary>
    [Theory]
    [InlineData("Survivor", "5")]
    [InlineData("Hunter", "12")]
    [InlineData("Outlaw", "7")]
    [InlineData("Saint", "12")]
    public void A_full_integer_tracker_with_the_flag_clear_is_available(string name, string stored)
    {
        var passage = Assert.Single(DeathPersistentReader.ParseWinState(name + "<egA>0<egA>" + stored));

        Assert.False(passage.Consumed);
        Assert.Equal(true, passage.Goal.Fulfilled);
    }

    // ---- IntegerTracker: progress against the maximum, not a tally ----

    /// <summary>
    /// The maxima come from WinState.CreateAndAddTracker: Survivor 5, Outlaw 7, and Hunter, Monk
    /// and Saint 12 each.
    /// </summary>
    [Theory]
    [InlineData("Survivor", "5", 5, 5, true, "5 / 5")]
    [InlineData("Survivor", "1", 1, 5, false, "1 / 5")]
    [InlineData("Outlaw", "7", 7, 7, true, "7 / 7")]
    [InlineData("Outlaw", "-3", -3, 7, false, "-3 / 7")]
    [InlineData("Hunter", "12", 12, 12, true, "12 / 12")]
    [InlineData("Monk", "2", 2, 12, false, "2 / 12")]
    [InlineData("Saint", "-14", -14, 12, false, "-14 / 12")]
    public void An_integer_tracker_reads_as_progress_towards_its_maximum(
        string name, string stored, int done, int needed, bool fulfilled, string text)
    {
        var goal = PassageGoals.Read(name, stored);

        Assert.Equal(PassageTracker.Count, goal.Tracker);
        Assert.Equal(done, goal.Done);
        Assert.Equal(needed, goal.Needed);
        Assert.Equal(fulfilled, goal.Fulfilled);
        Assert.Equal(text, goal.Text);
    }

    /// <summary>
    /// Artificer stores "Hunter&lt;egA&gt;0&lt;egA&gt;-147". WinState.DeathModifyTracker subtracts
    /// from progress on death, so a number this far below zero is ordinary, and reading it as a
    /// count of times the passage was taken is not.
    /// </summary>
    [Fact]
    public void A_tracker_driven_far_below_zero_by_deaths_is_still_progress()
    {
        var goal = PassageGoals.Read("Hunter", "-147");

        Assert.Equal(-147, goal.Done);
        Assert.Equal(false, goal.Fulfilled);
        Assert.Equal("-147 / 12", goal.Text);
    }

    // ---- ListTracker: a dot separated list of item ids ----

    /// <summary>
    /// Survivor's Scholar entry is "Scholar&lt;egA&gt;0&lt;egA&gt;17". WinState.ListTracker.ToString
    /// joins myList with a dot, so that is a one item list holding pearl id 17, one of the three
    /// the passage needs, and not seventeen of anything.
    /// </summary>
    [Fact]
    public void A_single_item_list_is_one_item_and_not_its_id()
    {
        var goal = PassageGoals.Read("Scholar", "17");

        Assert.Equal(PassageTracker.Items, goal.Tracker);
        Assert.Equal(1, goal.Done);
        Assert.Equal(3, goal.Needed);
        Assert.Equal(false, goal.Fulfilled);
        Assert.Equal("1 / 3", goal.Text);
    }

    [Theory]
    [InlineData("Scholar", "30.29", 2, 3, false)]
    [InlineData("Scholar", "25.18.20", 3, 3, true)]
    [InlineData("Scholar", "27", 1, 3, false)]
    [InlineData("Nomad", "2", 1, 4, false)]
    [InlineData("Nomad", "12", 1, 4, false)]
    [InlineData("DragonSlayer", "3.7.1.4.0.5", 6, 6, true)]
    [InlineData("DragonSlayer", "2.7.1.4.3", 5, 6, false)]
    [InlineData("DragonSlayer", "4.1", 2, 6, false)]
    public void A_list_tracker_counts_its_items_against_the_number_the_passage_needs(
        string name, string stored, int done, int needed, bool fulfilled)
    {
        var goal = PassageGoals.Read(name, stored);

        Assert.Equal(PassageTracker.Items, goal.Tracker);
        Assert.Equal(done, goal.Done);
        Assert.Equal(needed, goal.Needed);
        Assert.Equal(fulfilled, goal.Fulfilled);
    }

    // ---- BoolArrayTracker: one dot terminated flag each ----

    /// <summary>
    /// A flag array ends in a separator and a list does not, which is the only thing that tells
    /// Survivor's "DragonSlayer&lt;egA&gt;0&lt;egA&gt;0.0.1.1.0.0." apart from Gourmand's
    /// "DragonSlayer&lt;egA&gt;1&lt;egA&gt;3.7.1.4.0.5". Both are six long and mean different things.
    /// </summary>
    [Fact]
    public void A_dragon_slayer_flag_array_is_not_read_as_a_list_of_kills()
    {
        var flags = PassageGoals.Read("DragonSlayer", "0.0.1.1.0.0.");

        Assert.Equal(PassageTracker.Flags, flags.Tracker);
        Assert.Equal(2, flags.Done);
        Assert.Equal(6, flags.Needed);
        Assert.Equal(false, flags.Fulfilled);

        var list = PassageGoals.Read("DragonSlayer", "3.7.1.4.0.5");

        Assert.Equal(PassageTracker.Items, list.Tracker);
        Assert.Equal(6, list.Done);
    }

    [Theory]
    [InlineData("Traveller", "0.0.0.1.0.1.1.1.1.1.1.1.", 8, 12, false)]
    [InlineData("Traveller", "1.1.1.1.1.1.1.1.1.1.1.1.", 12, 12, true)]
    [InlineData("Pilgrim", "0.0.0.0.1.0.", 1, 6, false)]
    [InlineData("Pilgrim", "1.1.1.1.1.1.1.", 7, 7, true)]
    public void A_flag_array_counts_the_flags_that_are_set(
        string name, string stored, int done, int needed, bool fulfilled)
    {
        var goal = PassageGoals.Read(name, stored);

        Assert.Equal(PassageTracker.Flags, goal.Tracker);
        Assert.Equal(done, goal.Done);
        Assert.Equal(needed, goal.Needed);
        Assert.Equal(fulfilled, goal.Fulfilled);
    }

    /// <summary>
    /// Gourmand's tracker is one food count per type, and GourFeastTracker.GoalFullfilled needs
    /// every one of them above 0.
    /// </summary>
    [Fact]
    public void A_gourmand_feast_needs_every_food_type_eaten()
    {
        var full = PassageGoals.Read("Gourmand", "1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.");

        Assert.Equal(PassageTracker.Feast, full.Tracker);
        Assert.Equal(22, full.Done);
        Assert.Equal(22, full.Needed);
        Assert.Equal(true, full.Fulfilled);

        var partial = PassageGoals.Read("Gourmand", "1.0.3.1.");

        Assert.Equal(3, partial.Done);
        Assert.Equal(4, partial.Needed);
        Assert.Equal(false, partial.Fulfilled);
    }

    // ---- FloatTracker: a fraction that has to reach 1 ----

    [Theory]
    [InlineData("Chieftain", "0.65", false)]
    [InlineData("Chieftain", "1", true)]
    [InlineData("Martyr", "0.01875", false)]
    [InlineData("Martyr", "1", true)]
    [InlineData("Friend", "0", false)]
    [InlineData("Mother", "1", true)]
    public void A_fraction_tracker_is_fulfilled_when_it_reaches_one(
        string name, string stored, bool fulfilled)
    {
        var goal = PassageGoals.Read(name, stored);

        Assert.Equal(PassageTracker.Fraction, goal.Tracker);
        Assert.Equal(fulfilled, goal.Fulfilled);

        // The stored number is what the chip shows. Rounding it would hide a run that is one
        // hundredth away from the passage.
        Assert.Equal(stored, goal.Text);
    }

    // ---- Passages this app has no requirement for ----

    /// <summary>
    /// The Watcher slot carries StoredStumps, StoredCorn, Glutton and P1food, none of which is a
    /// WinState.EndgameID in the game's own assembly. A mod added them, so nothing here knows what
    /// they need, and saying they are unearned would be an answer invented out of nothing.
    /// </summary>
    [Theory]
    [InlineData("Glutton", "20")]
    [InlineData("P1food", "6")]
    [InlineData("StoredStumps", "0")]
    [InlineData("StoredCorn", "0")]
    public void A_passage_from_a_mod_keeps_its_stored_text_and_claims_nothing(string name, string stored)
    {
        var goal = PassageGoals.Read(name, stored);

        Assert.Equal(PassageTracker.Unknown, goal.Tracker);
        Assert.Null(goal.Needed);
        Assert.Null(goal.Fulfilled);
        Assert.Equal(stored, goal.Text);
    }

    // ---- Nothing to read ----

    [Fact]
    public void An_entry_with_no_tracker_text_claims_nothing()
    {
        var goal = PassageGoals.Read("Survivor", "");

        Assert.Null(goal.Done);
        Assert.Null(goal.Fulfilled);
        Assert.Equal("", goal.Text);
    }

    [Fact]
    public void A_null_name_and_a_null_progress_read_as_empty_rather_than_throwing()
    {
        var goal = PassageGoals.Read(null, null);

        Assert.Equal(PassageTracker.Unknown, goal.Tracker);
        Assert.Null(goal.Fulfilled);
        Assert.Equal("", goal.Text);
    }

    [Fact]
    public void An_integer_tracker_holding_something_that_is_not_a_number_claims_nothing()
    {
        var goal = PassageGoals.Read("Survivor", "quite far along");

        Assert.Equal(PassageTracker.Count, goal.Tracker);
        Assert.Null(goal.Done);
        Assert.Equal(5, goal.Needed);
        Assert.Null(goal.Fulfilled);
        Assert.Equal("quite far along", goal.Text);
    }

    [Fact]
    public void A_fraction_tracker_holding_something_that_is_not_a_number_claims_nothing()
    {
        var goal = PassageGoals.Read("Chieftain", "half way");

        Assert.Null(goal.Fulfilled);
        Assert.Equal("half way", goal.Text);
    }

    // ---- The record carries the reading ----

    [Fact]
    public void A_passage_record_reads_its_own_goal_from_the_name_and_the_stored_text()
    {
        var passage = new PassageRecord("Outlaw", false) { Progress = "7" };

        Assert.Equal(7, passage.Goal.Done);
        Assert.Equal(7, passage.Goal.Needed);
        Assert.Equal(true, passage.Goal.Fulfilled);
    }
}
