// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

public enum ModSyncAction
{
    TurnOn,

    TurnOff,

    Install,

    Matches,
}

public sealed class ModSyncRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ModSyncAction Action { get; init; }

    public string? Version { get; init; }

    public string? RecordedVersion { get; init; }

    public string? WorkshopId { get; init; }

    public int? RecordedLoadOrder { get; init; }

    public ModEntry? OnDisk { get; init; }

    public bool Include { get; set; } = true;

    public bool IsChange => Action is ModSyncAction.TurnOn or ModSyncAction.TurnOff;
}

public sealed record ModSyncOutcome(
    IReadOnlyList<string> EnabledIds,
    IReadOnlyDictionary<string, int> LoadOrder,
    IReadOnlyList<ModEntry> TurnOn,
    IReadOnlyList<ModEntry> TurnOff);

public sealed record ModSyncPlan(IReadOnlyList<ModSyncRow> Rows, ModListDiff Diff)
{
    public IEnumerable<ModSyncRow> Changes => Rows.Where(row => row.IsChange);

    public IEnumerable<ModSyncRow> Missing => Rows.Where(row => row.Action == ModSyncAction.Install);

    public bool NothingToDo => !Changes.Any(row => row.Include);

    public static ModSyncPlan Build(ModListSnapshot? recorded, CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        ModListDiff diff = ModListDiff.Compare(recorded, current);

        var installed = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in current.Installed)
        {
            installed.TryAdd(mod.Id, mod);
        }

        var recordedOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in recorded?.Mods ?? new List<ModEntry>())
        {
            if (mod.LoadOrder is { } position)
            {
                recordedOrder.TryAdd(mod.Id, position);
            }
        }

        var rows = new List<ModSyncRow>();

        foreach (ModEntry mod in diff.TurnedOff)
        {
            installed.TryGetValue(mod.Id, out ModEntry? local);
            rows.Add(Row(mod, ModSyncAction.TurnOn, local, recordedOrder));
        }

        foreach (ModEntry mod in diff.Missing)
        {
            rows.Add(Row(mod, ModSyncAction.Install, null, recordedOrder));
        }

        foreach (ModEntry mod in diff.Extra)
        {
            installed.TryGetValue(mod.Id, out ModEntry? local);
            rows.Add(Row(mod, ModSyncAction.TurnOff, local ?? mod, recordedOrder));
        }

        // A version that moved is still a mod that is on and was recorded, so it sits with the rest
        // of the matches and carries both versions for the row to say so.
        var moved = diff.Changed.ToDictionary(change => change.Id, StringComparer.OrdinalIgnoreCase);

        foreach (ModEntry mod in current.Enabled.Mods)
        {
            if (rows.Any(row => string.Equals(row.Id, mod.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            moved.TryGetValue(mod.Id, out ModVersionChange? change);
            installed.TryGetValue(mod.Id, out ModEntry? local);

            rows.Add(new ModSyncRow
            {
                Id = mod.Id,
                Name = Pick(mod.Name, mod.Id),
                Action = ModSyncAction.Matches,
                Version = mod.Version,
                RecordedVersion = change?.Recorded,
                WorkshopId = mod.WorkshopId ?? local?.WorkshopId,
                OnDisk = local ?? mod,
            });
        }

        return new ModSyncPlan(rows, diff);
    }

    public ModSyncOutcome Resolve(CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var enabled = new List<string>(current.Enabled.Mods.Select(mod => mod.Id));
        var seen = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        var loadOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var turnOn = new List<ModEntry>();
        var turnOff = new List<ModEntry>();

        foreach (ModSyncRow row in Rows)
        {
            if (!row.Include)
            {
                continue;
            }

            switch (row.Action)
            {
                case ModSyncAction.TurnOn when row.OnDisk is { } mod:
                    if (seen.Add(row.Id))
                    {
                        enabled.Add(row.Id);
                    }

                    if (row.RecordedLoadOrder is { } position)
                    {
                        loadOrder[row.Id] = position;
                    }

                    turnOn.Add(mod);
                    break;

                case ModSyncAction.TurnOff:
                    enabled.RemoveAll(id => string.Equals(id, row.Id, StringComparison.OrdinalIgnoreCase));
                    seen.Remove(row.Id);

                    if (row.OnDisk is { } off)
                    {
                        turnOff.Add(off);
                    }

                    break;
            }
        }

        return new ModSyncOutcome(enabled, loadOrder, turnOn, turnOff);
    }

    private static ModSyncRow Row(
        ModEntry mod,
        ModSyncAction action,
        ModEntry? local,
        IReadOnlyDictionary<string, int> recordedOrder)
        => new()
        {
            Id = mod.Id,
            Name = Pick(local?.Name, mod.Name, mod.Id),
            Action = action,
            Version = local?.Version ?? mod.Version,
            RecordedVersion = mod.Version,
            WorkshopId = local?.WorkshopId ?? mod.WorkshopId,
            RecordedLoadOrder = recordedOrder.TryGetValue(mod.Id, out int position) ? position : mod.LoadOrder,
            OnDisk = local,
        };

    private static string Pick(params string?[] candidates)
        => candidates.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";
}
