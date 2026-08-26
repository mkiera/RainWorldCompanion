// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

public sealed record ModVersionChange(string Id, string Name, string? Recorded, string? Now, string? WorkshopId);

/// <summary>
/// Informational, always: nothing here becomes a plan problem or refuses a load. The three flags
/// matter as much as the four lists, because nothing differing and nothing being known both produce
/// four empty lists, and showing the second as the first would claim a match nobody checked.
/// </summary>
/// <param name="Missing">Recorded, and not installed on this machine now.</param>
/// <param name="TurnedOff">Recorded, installed now, but not turned on.</param>
/// <param name="Changed">On now, at a different version than was recorded.</param>
/// <param name="Extra">On now, and not in the recording.</param>
public sealed record ModListDiff(
    IReadOnlyList<ModEntry> Missing,
    IReadOnlyList<ModEntry> TurnedOff,
    IReadOnlyList<ModVersionChange> Changed,
    IReadOnlyList<ModEntry> Extra,
    string? RecordedGameVersion,
    string? CurrentGameVersion,
    IReadOnlyList<string> Notes)
{
    /// <summary>The snapshot carries no mod list, because it predates them being recorded.</summary>
    public bool NothingWasRecorded { get; init; }

    /// <summary>The snapshot recorded that it could not read the mods that were on.</summary>
    public bool RecordedCouldNotLook { get; init; }

    public bool CurrentCouldNotLook { get; init; }

    public int RecordedCount { get; init; }

    public int CurrentCount { get; init; }

    /// <summary>True only when both versions are known and they disagree.</summary>
    public bool GameVersionDiffers =>
        RecordedGameVersion is { Length: > 0 } recorded
        && CurrentGameVersion is { Length: > 0 } now
        && !string.Equals(recorded, now, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the four lists are empty, which only means a match if something was compared.</summary>
    public bool ListsMatch =>
        Missing.Count == 0 && TurnedOff.Count == 0 && Changed.Count == 0 && Extra.Count == 0;

    public bool Compared => !NothingWasRecorded && !RecordedCouldNotLook && !CurrentCouldNotLook;

    public bool Matches => Compared && ListsMatch && !GameVersionDiffers;

    /// <summary>Always returns a diff, with the flags saying how much of it means anything.</summary>
    public static ModListDiff Compare(ModListSnapshot? recorded, CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        ModListSnapshot now = current.Enabled;

        // A recording that could not be read is not a recording of an empty machine, and diffing it
        // as one would report every mod as removed.
        if (recorded is null || !recorded.ReadTheEnabledList || !now.ReadTheEnabledList)
        {
            return new ModListDiff(
                Array.Empty<ModEntry>(),
                Array.Empty<ModEntry>(),
                Array.Empty<ModVersionChange>(),
                Array.Empty<ModEntry>(),
                recorded?.GameVersion,
                now.GameVersion,
                Array.Empty<string>())
            {
                NothingWasRecorded = recorded is null,
                RecordedCouldNotLook = recorded is not null && !recorded.ReadTheEnabledList,
                CurrentCouldNotLook = !now.ReadTheEnabledList,
                RecordedCount = recorded?.Mods.Count ?? 0,
                CurrentCount = now.Mods.Count,
            };
        }

        var enabledNow = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in now.Mods)
        {
            enabledNow.TryAdd(mod.Id, mod);
        }

        var installedNow = new HashSet<string>(
            current.Installed.Select(mod => mod.Id),
            StringComparer.OrdinalIgnoreCase);

        var missing = new List<ModEntry>();
        var turnedOff = new List<ModEntry>();
        var changed = new List<ModVersionChange>();
        var notes = new List<string>();

        foreach (ModEntry was in recorded.Mods)
        {
            if (enabledNow.TryGetValue(was.Id, out ModEntry? isOn))
            {
                if (VersionMoved(was.Version, isOn.Version))
                {
                    changed.Add(new ModVersionChange(
                        isOn.Id,
                        Pick(isOn.Name, was.Name, was.Id),
                        was.Version,
                        isOn.Version,
                        isOn.WorkshopId ?? was.WorkshopId));
                }

                continue;
            }

            // Installed but off is only claimed when the install was actually looked at.
            if (now.CheckedTheInstall && installedNow.Contains(was.Id))
            {
                turnedOff.Add(was);
                continue;
            }

            missing.Add(was);
        }

        if (!now.CheckedTheInstall && missing.Count > 0)
        {
            notes.Add("The game folder was not read, so a mod listed as not installed may only be turned off.");
        }

        if (!recorded.CheckedTheInstall && recorded.Mods.Count > 0)
        {
            notes.Add("No versions were recorded with this list, so only which mods were on can be compared.");
        }

        var recordedIds = new HashSet<string>(
            recorded.Mods.Select(mod => mod.Id),
            StringComparer.OrdinalIgnoreCase);

        List<ModEntry> extra = now.Mods.Where(mod => !recordedIds.Contains(mod.Id)).ToList();

        return new ModListDiff(
            Sort(missing),
            Sort(turnedOff),
            changed.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Sort(extra),
            recorded.GameVersion,
            now.GameVersion,
            notes)
        {
            RecordedCount = recorded.Mods.Count,
            CurrentCount = now.Mods.Count,
        };
    }

    /// <summary>Only counts as moved when both sides have a version: a mod that ships without one
    /// has nothing to compare.</summary>
    private static bool VersionMoved(string? recorded, string? now)
        => recorded is { } was && now is { } current
            && was.Trim().Length > 0 && current.Trim().Length > 0
            && !string.Equals(was.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Pick(params string?[] candidates)
        => candidates.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";

    /// <summary>By name then id. Load order would be the game's answer, but half of these lists hold
    /// mods the game has no order for.</summary>
    private static List<ModEntry> Sort(List<ModEntry> mods)
        => mods
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
