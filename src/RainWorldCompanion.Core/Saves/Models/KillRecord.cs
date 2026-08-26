// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text.Json.Serialization;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Saves.Models;

/// <param name="CreatureId">The id exactly as stored, for example "Fly-Creature-0". The suffix after
/// the first hyphen is the game's own creature template bookkeeping.</param>
/// <param name="DisplayName">The text before the first hyphen, for example "Fly".</param>
public sealed record KillRecord(string CreatureId, string DisplayName, int Count);

/// <summary>One entry of the WINSTATE field inside DEATHPERSISTENTSAVEDATA.</summary>
/// <param name="Name">Passage name, for example "Survivor" or "Saint".</param>
/// <param name="Consumed">Set once the player has used the passage to travel, so it marks one the
/// game no longer offers rather than one the player has earned. Whether it is earned is not stored
/// at all: it is <see cref="Goal"/>.</param>
public sealed record PassageRecord(string Name, bool Consumed)
{
    private readonly string _progress = "";

    /// <summary>The tracker exactly as stored, for example "17", "30.29", "0.65" or "1.1.0.1.". The
    /// game has five tracker shapes, so the raw text is kept and <see cref="PassageGoals"/> reads it
    /// against the passage name. Empty when the entry carried no third part.</summary>
    public string Progress
    {
        get => _progress;
        init => _progress = value ?? "";
    }

    [JsonIgnore]
    public PassageGoal Goal => PassageGoals.Read(Name, Progress);
}

/// <summary>One entry of the GHOSTS field inside DEATHPERSISTENTSAVEDATA.</summary>
/// <param name="RegionCode">Two letter region code, for example "SH" or "UW".</param>
/// <param name="State">2 for an echo the player has spoken to and 1 for one they only have a hunch
/// about. Nothing in the game adds to it, so it is a state and not a tally.</param>
public sealed record EchoRecord(string RegionCode, int State)
{
    /// <summary>The value SaveState.GhostEncounter stores once the player has spoken to the echo.</summary>
    public const int TalkedTo = 2;

    /// <summary>The value GhostHunch.Update stores for an echo the player has only sensed.</summary>
    public const int Hunch = 1;
}
