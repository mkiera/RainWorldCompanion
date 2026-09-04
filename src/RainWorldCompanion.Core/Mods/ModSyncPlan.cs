// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

public sealed class ModSyncRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? RecordedVersion { get; init; }

    public string? WorkshopId { get; init; }

    public int? RecordedLoadOrder { get; init; }

    public int? CurrentLoadOrder { get; init; }

    public int? WantedLoadOrder { get; set; }

    public ModEntry? OnDisk { get; init; }

    // False for a mod a recording names that is nowhere on this machine. Nothing can turn one on,
    // so Wanted stays where it starts.
    public required bool Installed { get; init; }

    public required bool IsOn { get; init; }

    // True when the recording had this mod, which is what separates a mod the save wanted from one
    // that happens to be lying around.
    public bool Recorded { get; init; }

    public bool Wanted { get; set; }

    public bool Changes => Installed && Wanted != IsOn;

    public bool TurningOn => Changes && Wanted;

    public bool TurningOff => Changes && !Wanted;

    public bool OrderChanges =>
        Installed && IsOn && Wanted && WantedLoadOrder is not null && WantedLoadOrder != CurrentLoadOrder;
}

public sealed record ModSyncOutcome(
    IReadOnlyList<string> EnabledIds,
    IReadOnlyDictionary<string, int> LoadOrder,
    IReadOnlyList<ModEntry> TurnOn,
    IReadOnlyList<ModEntry> TurnOff);

public sealed record ModSyncPlan(IReadOnlyList<ModSyncRow> Rows, ModListDiff Diff)
{
    public IEnumerable<ModSyncRow> Installed => Rows.Where(row => row.Installed);

    public IEnumerable<ModSyncRow> NotInstalled => Rows.Where(row => !row.Installed);

    public IEnumerable<ModSyncRow> Changing => Rows.Where(row => row.Changes);

    public int OnCount => Rows.Count(row => row.Installed && row.Wanted);

    public bool NothingToDo => !Changing.Any() && !Rows.Any(row => row.OrderChanges);

    // Wanted starts where the recording puts it, or at what is on now when there is no recording,
    // so opening the window and pressing Apply without touching anything writes nothing.
    public static ModSyncPlan Build(ModListSnapshot? recorded, CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        ModListDiff diff = ModListDiff.Compare(recorded, current);

        var enabled = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in current.Enabled.Mods)
        {
            enabled.TryAdd(mod.Id, mod);
        }

        var wasRecorded = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in recorded?.Mods ?? new List<ModEntry>())
        {
            wasRecorded.TryAdd(mod.Id, mod);
        }

        bool matching = recorded is { ReadTheEnabledList: true };
        var rows = new List<ModSyncRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModEntry mod in current.Installed)
        {
            if (!seen.Add(mod.Id))
            {
                continue;
            }

            bool isOn = enabled.TryGetValue(mod.Id, out ModEntry? onNow);
            wasRecorded.TryGetValue(mod.Id, out ModEntry? was);

            rows.Add(new ModSyncRow
            {
                Id = mod.Id,
                Name = Pick(mod.Name, mod.Id),
                Version = mod.Version,
                RecordedVersion = was?.Version,
                WorkshopId = mod.WorkshopId ?? was?.WorkshopId,
                RecordedLoadOrder = was?.LoadOrder,
                CurrentLoadOrder = onNow?.LoadOrder,
                WantedLoadOrder = matching ? was?.LoadOrder : onNow?.LoadOrder,
                OnDisk = mod,
                Installed = true,
                IsOn = isOn,
                Recorded = was is not null,
                Wanted = matching ? was is not null : isOn,
            });
        }

        // A mod the game has on that is nowhere on disk. It cannot be turned on, but it can be
        // taken out of the list, so it is a row rather than a silent omission.
        foreach (ModEntry mod in current.Enabled.Mods)
        {
            if (!seen.Add(mod.Id))
            {
                continue;
            }

            rows.Add(new ModSyncRow
            {
                Id = mod.Id,
                Name = Pick(mod.Name, mod.Id),
                Version = mod.Version,
                WorkshopId = mod.WorkshopId,
                Installed = false,
                IsOn = true,
                Recorded = wasRecorded.ContainsKey(mod.Id),
                Wanted = matching && wasRecorded.ContainsKey(mod.Id),
            });
        }

        foreach (ModEntry mod in recorded?.Mods ?? new List<ModEntry>())
        {
            if (!seen.Add(mod.Id))
            {
                continue;
            }

            rows.Add(new ModSyncRow
            {
                Id = mod.Id,
                Name = Pick(mod.Name, mod.Id),
                RecordedVersion = mod.Version,
                WorkshopId = mod.WorkshopId,
                RecordedLoadOrder = mod.LoadOrder,
                WantedLoadOrder = matching ? mod.LoadOrder : null,
                Installed = false,
                IsOn = false,
                Recorded = true,
                Wanted = matching,
            });
        }

        return new ModSyncPlan(
            rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            diff);
    }

    public ModSyncOutcome Resolve(CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var wanted = new HashSet<string>(
            Rows.Where(row => row.Installed && row.Wanted).Select(row => row.Id),
            StringComparer.OrdinalIgnoreCase);

        var enabled = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What is on now keeps its place, so the options file rewrites the lines that moved and no
        // others. A mod nothing installed is dropped, because the game cannot load it anyway.
        foreach (ModEntry mod in current.Enabled.Mods)
        {
            if (wanted.Contains(mod.Id) && seen.Add(mod.Id))
            {
                enabled.Add(mod.Id);
            }
        }

        foreach (ModSyncRow row in Rows.Where(row => row.Installed && row.Wanted))
        {
            if (seen.Add(row.Id))
            {
                enabled.Add(row.Id);
            }
        }

        var loadOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var turnOn = new List<ModEntry>();
        var turnOff = new List<ModEntry>();

        foreach (ModSyncRow row in Rows)
        {
            if (row.Installed && row.Wanted && row.WantedLoadOrder is { } position)
            {
                loadOrder[row.Id] = position;
            }

            if (row.TurningOn && row.OnDisk is { } coming)
            {
                turnOn.Add(coming);
            }
            else if (row.TurningOff && row.OnDisk is { } going)
            {
                turnOff.Add(going);
            }
        }

        return new ModSyncOutcome(enabled, loadOrder, turnOn, turnOff);
    }

    public void WantEverythingOnNow()
    {
        foreach (ModSyncRow row in Rows)
        {
            row.Wanted = row.IsOn;
            row.WantedLoadOrder = row.CurrentLoadOrder;
        }
    }

    public void WantWhatTheSaveHad()
    {
        foreach (ModSyncRow row in Rows)
        {
            row.Wanted = row.Recorded;
            row.WantedLoadOrder = row.RecordedLoadOrder;
        }
    }

    private static string Pick(params string?[] candidates)
        => candidates.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";
}
