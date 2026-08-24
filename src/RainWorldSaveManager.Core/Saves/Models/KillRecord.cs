// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldSaveManager.Core.Saves.Models;

/// <summary>
/// One line of the KILLS field: how many of one creature this campaign has killed.
/// </summary>
/// <param name="CreatureId">
/// The id exactly as stored, for example "Fly-Creature-0". The suffix after the first hyphen is
/// the game's own creature template bookkeeping and is kept so nothing is lost.
/// </param>
/// <param name="DisplayName">The text before the first hyphen, for example "Fly".</param>
/// <param name="Count">Kills recorded for this creature.</param>
public sealed record KillRecord(string CreatureId, string DisplayName, int Count);

/// <summary>
/// One entry of the WINSTATE field inside DEATHPERSISTENTSAVEDATA: an endgame passage and
/// whether the player has earned it.
/// </summary>
/// <param name="Name">Passage name, for example "Survivor" or "Saint".</param>
/// <param name="Earned">True when the stored earned flag is 1.</param>
/// <param name="Count">
/// Times the passage has been taken, when the stored tracker is a plain integer. It is 0 for
/// every other shape, so a caller that wants to tell "no progress" from "progress this app did
/// not read as a number" has to look at <see cref="PassageRecord.Progress"/>.
/// </param>
public sealed record PassageRecord(string Name, bool Earned, int Count)
{
    private readonly string _progress = "";

    /// <summary>
    /// The tracker exactly as the save stored it, for example "17", "30.29" or
    /// "0.65". The game writes plain ints, floats and dotted flag strings here depending on the
    /// passage, and only the int form reaches <see cref="Count"/>. Keeping the raw text means a
    /// value that is not an int shows as what it is rather than as nothing at all.
    ///
    /// Empty when the entry carried no third part.
    /// </summary>
    public string Progress
    {
        get => _progress;
        init => _progress = value ?? "";
    }
}

/// <summary>
/// One entry of the GHOSTS field inside DEATHPERSISTENTSAVEDATA: an echo the player has met.
/// </summary>
/// <param name="RegionCode">Two letter region code, for example "SH" or "UW".</param>
/// <param name="Count">The stored counter for that echo.</param>
public sealed record EchoRecord(string RegionCode, int Count);
