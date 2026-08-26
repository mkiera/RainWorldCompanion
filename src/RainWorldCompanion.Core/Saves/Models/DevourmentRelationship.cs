// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves.Models;

/// <summary>One DEVOURMENTSTATE field, written by the Devourment mod and absent from a vanilla save.</summary>
/// <param name="Status">Belly status name: Held, Digesting, Healing, EnergyTheft or Sedating.</param>
/// <param name="FoodValue">Food this prey is worth, as stored. Items store -1, which the mod uses to
/// mean no food value. Null only when the stored text is not an integer.</param>
/// <param name="PredatorId">This is what links one relationship to another: a predator carrying a
/// creature that is itself carrying something appears twice under the same id. Empty when the blob
/// carried no id, which leaves the row flat.</param>
/// <param name="PredatorDetail">Null when the blob was not read, which is what a manifest written
/// before this was recorded looks like.</param>
public sealed record DevourmentRelationship(
    string PredatorType,
    string PreyType,
    string Status,
    int? FoodValue,
    bool PreyIsItem,
    string PredatorId = "",
    string PreyId = "",
    DevourmentEntity? PredatorDetail = null,
    DevourmentEntity? PreyDetail = null);
