// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldSaveManager.Core.Saves.Models;

/// <summary>
/// One DEVOURMENTSTATE field: something a predator is currently carrying, and what is being
/// done with it. Written by the Devourment mod, so the field is absent from a vanilla save.
/// </summary>
/// <param name="PredatorType">Creature type doing the carrying, for example "Slugcat".</param>
/// <param name="PreyType">
/// Creature type when <paramref name="PreyIsItem"/> is false, for example "PinkLizard", and
/// item type when it is true, for example "Rock" or "DataPearl".
/// </param>
/// <param name="Status">Belly status name: Held, Digesting, Healing, EnergyTheft or Sedating.</param>
/// <param name="FoodValue">
/// Food this prey is worth, as stored. Items store -1, which the mod uses to mean no food value.
/// Null only when the stored text is not an integer.
/// </param>
/// <param name="PreyIsItem">True when the prey is an item rather than a creature.</param>
/// <param name="PredatorId">
/// Entity id of the predator, for example "ID.-1.0" for the player. This is what links one
/// relationship to another: a predator carrying a creature that is itself carrying something
/// appears twice, once as the prey of the outer pair and once as the predator of the inner one,
/// under the same id both times. Empty when the blob carried no id, which leaves the row flat.
/// </param>
/// <param name="PreyId">Entity id of the prey, in the same form as <paramref name="PredatorId"/>.</param>
public sealed record DevourmentRelationship(
    string PredatorType,
    string PreyType,
    string Status,
    int? FoodValue,
    bool PreyIsItem,
    string PredatorId = "",
    string PreyId = "");
