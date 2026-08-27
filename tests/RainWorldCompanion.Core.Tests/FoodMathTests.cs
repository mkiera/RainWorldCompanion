// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the Core assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text.Json;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// FOOD on disk can be negative: SaveState.SessionEnded stores clamped food minus
/// foodToHibernate, LoadGame reads it back unclamped, and both consumers (the RainWorldGame
/// constructor and SlugcatPageContinue) ignore anything below 1. Raw
/// <see cref="CampaignSummary.Food"/> stays as it sits on disk because that is what an editor
/// writes back, so everything the UI shows is derived, and this suite pins that derivation.
/// </summary>
public class FoodMathTests
{
    /// <summary>
    /// The nine campaigns in the live sav, plus the Rain Meadow campaign that prompted this. Each
    /// one's stored FOOD and the pips a run starting from it gets. Only the Meadow Survivor stores
    /// a negative: its cycle ended with nothing banked, so SessionEnded wrote 0 minus Survivor's
    /// shelter cost of 4.
    /// </summary>
    [Theory]
    [InlineData("White", 2, 2, false)]
    [InlineData("Yellow", 0, 0, false)]
    [InlineData("Gourmand", 1, 1, false)]
    [InlineData("Artificer", 3, 3, false)]
    [InlineData("Rivulet", 1, 1, false)]
    [InlineData("Spear", 0, 0, false)]
    [InlineData("Saint", 1, 1, false)]
    [InlineData("Watcher", 1, 1, false)]
    [InlineData("Red", 3, 3, false)]
    [InlineData("White", -4, 0, true)]
    public void A_real_campaign_reports_the_food_the_run_starts_with(
        string slugcatId, int storedFood, int expectedEffective, bool expectedNegative)
    {
        var campaign = Campaign(storedFood, slugcatId);

        Assert.Equal(storedFood, campaign.Food);
        Assert.Equal(expectedEffective, campaign.EffectiveFood);
        Assert.Equal(expectedNegative, campaign.FoodStoredNegative);
    }

    /// <summary>
    /// Every one of those stored numbers sits between the negative of the shelter cost and the
    /// meter's capacity less that cost, which is the whole range SessionEnded's clamp-then-subtract
    /// can produce. Checking it against the ten real numbers keeps the table honest, where checking
    /// the table against itself would only repeat what was copied out of the assembly.
    /// </summary>
    [Theory]
    [InlineData("White", 2)]
    [InlineData("Yellow", 0)]
    [InlineData("Gourmand", 1)]
    [InlineData("Artificer", 3)]
    [InlineData("Rivulet", 1)]
    [InlineData("Spear", 0)]
    [InlineData("Saint", 1)]
    [InlineData("Watcher", 1)]
    [InlineData("Red", 3)]
    [InlineData("White", -4)]
    public void A_real_stored_food_sits_inside_what_the_write_path_can_produce(
        string slugcatId, int storedFood)
    {
        var meter = FoodMath.MeterFor(slugcatId);

        Assert.InRange(storedFood, -meter.PipsToHibernate, meter.MaxPips - meter.PipsToHibernate);
    }

    [Fact]
    public void Food_stored_below_zero_starts_the_run_with_nothing()
    {
        // online_sav2 holds this: Survivor, FOOD -4, TOTFOOD 11.
        var campaign = Campaign(-4);

        Assert.Equal(-4, campaign.Food);
        Assert.Equal(0, campaign.EffectiveFood);
        Assert.True(campaign.FoodStoredNegative);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(-6)]
    [InlineData(-12)]
    [InlineData(int.MinValue)]
    public void Any_food_below_zero_reads_as_zero(int storedFood)
    {
        var campaign = Campaign(storedFood);

        Assert.Equal(0, campaign.EffectiveFood);
        Assert.True(campaign.FoodStoredNegative);
    }

    [Fact]
    public void Food_of_zero_is_not_marked()
    {
        // A cycle that hibernated on exactly the shelter cost stores 0, and six of the ten real
        // campaigns sit at 0 or 1. Marking it would put an asterisk on the ordinary case.
        var campaign = Campaign(0);

        Assert.Equal(0, campaign.EffectiveFood);
        Assert.False(campaign.FoodStoredNegative);
    }

    /// <summary>
    /// Nothing bounds food from above on load. The RainWorldGame constructor copies the stored
    /// number into PlayerState.foodInStomach whole, so a hand-edited 99 really is 99 pips in play,
    /// even though the save select screen clamps it to the meter before drawing. Reporting the
    /// meter's capacity here would name a number the run does not have.
    /// </summary>
    [Theory]
    [InlineData("White", 8)]
    [InlineData("White", 99)]
    [InlineData("Saint", 6)]
    [InlineData("White", int.MaxValue)]
    public void Food_above_the_meter_is_left_alone(string slugcatId, int storedFood)
    {
        var campaign = Campaign(storedFood, slugcatId);

        Assert.Equal(storedFood, campaign.EffectiveFood);
        Assert.False(campaign.FoodStoredNegative);
    }

    [Fact]
    public void A_record_with_no_food_field_derives_nothing_from_it()
    {
        var campaign = Campaign(null);

        Assert.Null(campaign.Food);
        Assert.Null(campaign.EffectiveFood);
        Assert.False(campaign.FoodStoredNegative);
    }

    /// <summary>
    /// SlugcatStats.SlugcatFoodMeter, value for value. The pairs are the pips the meter holds and
    /// the pips a shelter costs, and the panel needs the second one to say why a stored number is
    /// negative.
    /// </summary>
    [Theory]
    [InlineData("White", 7, 4)]
    [InlineData("Yellow", 5, 3)]
    [InlineData("Red", 9, 6)]
    [InlineData("Rivulet", 6, 5)]
    [InlineData("Artificer", 9, 6)]
    [InlineData("Saint", 5, 4)]
    [InlineData("Spear", 10, 5)]
    [InlineData("Gourmand", 11, 7)]
    [InlineData("Slugpup", 3, 2)]
    [InlineData("Watcher", 7, 4)]
    public void The_meter_table_matches_the_game(string slugcatId, int maxPips, int pipsToHibernate)
    {
        Assert.Equal(new FoodMeter(maxPips, pipsToHibernate), FoodMath.MeterFor(slugcatId));
    }

    [Fact]
    public void Sofanthiel_is_looked_up_under_the_id_a_save_writes()
    {
        // MoreSlugcatsEnums.SlugcatStatsName registers Sofanthiel under the name "Inv", so that is
        // the id in "SAV STATE NUMBER", and it is the id the catalog carries too.
        Assert.Equal(new FoodMeter(12, 12), FoodMath.MeterFor("Inv"));
        Assert.Contains(SlugcatCatalog.Known, entry => entry.Id == "Inv");
    }

    [Fact]
    public void Every_slugcat_the_catalog_knows_has_its_own_meter()
    {
        foreach (var entry in SlugcatCatalog.Known)
        {
            // Survivor and Watcher share the game's fallback pair, so they are the two exceptions.
            if (entry.Id is "White" or "Watcher")
            {
                continue;
            }

            Assert.NotEqual(FoodMath.DefaultMeter, FoodMath.MeterFor(entry.Id));
        }
    }

    [Theory]
    [InlineData("white")]
    [InlineData("WHITE")]
    [InlineData("  White  ")]
    public void The_meter_lookup_ignores_case_and_surrounding_space(string slugcatId)
    {
        Assert.Equal(new FoodMeter(7, 4), FoodMath.MeterFor(slugcatId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomeModdedSlugcat")]
    public void An_unknown_slugcat_gets_the_meter_the_game_falls_back_to(string? slugcatId)
    {
        // SlugcatFoodMeter ends in an unconditional return of IntVector2(7, 4).
        Assert.Equal(FoodMath.DefaultMeter, FoodMath.MeterFor(slugcatId));
        Assert.Equal(new FoodMeter(7, 4), FoodMath.DefaultMeter);
    }

    [Fact]
    public void Describe_reports_the_food_the_run_starts_with_rather_than_the_stored_negative()
    {
        var text = new CampaignSummary { SlugcatId = "White", CycleNum = 4, Food = -4 }.Describe();

        Assert.Equal("White  cycle 4  food 0", text);
        Assert.DoesNotContain("-4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_still_reports_a_food_of_zero()
    {
        Assert.Equal(
            "White  cycle 4  food 0",
            new CampaignSummary { SlugcatId = "White", CycleNum = 4, Food = 0 }.Describe());
    }

    /// <summary>The camelCase names the derived properties would take if they were serialised.</summary>
    private static readonly string[] DerivedFoodJsonNames =
    {
        "effectiveFood", "foodStoredNegative", "foodMeter",
    };

    [Fact]
    public void A_manifest_records_the_stored_food_and_none_of_the_numbers_derived_from_it()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("today", null);

        using var document = JsonDocument.Parse(File.ReadAllText(snapshot.ManifestPath));

        var campaigns = document.RootElement.GetProperty("slots").EnumerateArray()
            .SelectMany(slot => slot.GetProperty("campaigns").EnumerateArray())
            .ToList();

        Assert.NotEmpty(campaigns);

        // The live tree is built from sav2.bin, which stores FOOD 3 in both its campaigns, and
        // sav3.bin, which stores 3 and 0. All four go into the file untouched.
        var stored = new List<int>();

        foreach (var campaign in campaigns)
        {
            Assert.True(campaign.TryGetProperty("food", out var food), "a campaign has no food");
            stored.Add(food.GetInt32());

            foreach (var name in DerivedFoodJsonNames)
            {
                Assert.False(
                    campaign.TryGetProperty(name, out _),
                    name + " was written to the manifest, but it is computed from food");
            }
        }

        Assert.Contains(3, stored);
        Assert.Contains(0, stored);
    }

    [Fact]
    public void A_campaign_reads_its_food_back_out_of_a_manifest_and_derives_the_rest_again()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("today", null);

        var reloaded = BackupSnapshot.Load(snapshot.DirectoryPath);
        var campaign = reloaded.Manifest!.Slots
            .SelectMany(slot => slot.Campaigns)
            .First(c => c.Food == 3);

        Assert.Equal(3, campaign.EffectiveFood);
        Assert.False(campaign.FoodStoredNegative);
        Assert.Equal(new FoodMeter(7, 4), campaign.FoodMeter);
    }

    private static CampaignSummary Campaign(int? food, string slugcatId = "White")
        => new() { SlugcatId = slugcatId, Food = food };
}
