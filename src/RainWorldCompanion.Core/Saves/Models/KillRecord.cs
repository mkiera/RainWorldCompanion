// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text.Json.Serialization;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Saves.Models;

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
/// One entry of the WINSTATE field inside DEATHPERSISTENTSAVEDATA: an endgame passage, whether it
/// has already been spent, and how far the run has got towards it.
/// </summary>
/// <param name="Name">Passage name, for example "Survivor" or "Saint".</param>
/// <param name="Consumed">
/// True when the stored flag is 1. WinState.EndgameTracker writes and reads this from its
/// <c>consumed</c> field, which is set once the player has used the passage to travel, so it marks
/// a passage the game no longer offers rather than one the player has earned. Whether the passage
/// is earned is not stored at all: it is <see cref="Goal"/>.
/// </param>
public sealed record PassageRecord(string Name, bool Consumed)
{
    private readonly string _progress = "";

    /// <summary>
    /// The tracker exactly as the save stored it, for example "17", "30.29", "0.65" or
    /// "1.1.0.1.". The game has five tracker shapes and writes whichever one the passage uses, so
    /// the raw text is kept and <see cref="PassageGoals"/> reads it against the passage name.
    ///
    /// Empty when the entry carried no third part.
    /// </summary>
    public string Progress
    {
        get => _progress;
        init => _progress = value ?? "";
    }

    /// <summary>
    /// What <see cref="Progress"/> amounts to for this passage: the progress, the requirement, and
    /// whether the requirement is met.
    ///
    /// Not serialised. It is worked out from <see cref="Name"/> and <see cref="Progress"/>, which
    /// the manifest already records, and it has no setter.
    /// </summary>
    [JsonIgnore]
    public PassageGoal Goal => PassageGoals.Read(Name, Progress);
}

/// <summary>
/// One entry of the GHOSTS field inside DEATHPERSISTENTSAVEDATA: an echo the player has met.
/// </summary>
/// <param name="RegionCode">Two letter region code, for example "SH" or "UW".</param>
/// <param name="State">
/// How far the encounter got. SaveState.GhostEncounter stores 2 for an echo the player has spoken
/// to and GhostHunch.Update stores 1 for one the player only has a hunch about. Nothing in the
/// game adds to it, so it is a state and not a tally.
/// </param>
public sealed record EchoRecord(string RegionCode, int State)
{
    /// <summary>The value SaveState.GhostEncounter stores once the player has spoken to the echo.</summary>
    public const int TalkedTo = 2;

    /// <summary>The value GhostHunch.Update stores for an echo the player has only sensed.</summary>
    public const int Hunch = 1;
}
