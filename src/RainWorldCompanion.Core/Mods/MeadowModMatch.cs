// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
namespace RainWorldCompanion.Core.Mods;

// The two newline-joined lists a lobby advertises under its "mods" and "banned_mods" keys.
public sealed record MeadowLobbyMods
{
    public static MeadowLobbyMods None { get; } = new();

    public IReadOnlyList<string> Required { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Banned { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Required.Count == 0 && Banned.Count == 0;

    public static MeadowLobbyMods Read(string? mods, string? bannedMods) => new()
    {
        Required = Split(mods),
        Banned = Split(bannedMods),
    };

    // ModArrayToString joins on "\n" and nothing escapes, so an empty entry is possible and is
    // dropped the same way Rain Meadow drops it before comparing.
    private static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string part in value.Split('\n'))
        {
            string id = part.Trim();
            if (id.Length > 0 && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}

// What Rain Meadow would make you change to join a lobby, worked out the same way
// RainMeadowModManager.CheckMods works it out, so applying this leaves it nothing to do.
public sealed record MeadowModMatch
{
    public IReadOnlyList<string> Enable { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Disable { get; init; } = Array.Empty<string>();

    // Named in the lobby's list and nowhere on this machine. Nothing can turn one on.
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();

    public bool Reorders { get; init; }

    // The lobby's required ids, in the order it listed them.
    public IReadOnlyList<string> Order { get; init; } = Array.Empty<string>();

    public bool NothingToDo =>
        Enable.Count == 0 && Disable.Count == 0 && Missing.Count == 0 && !Reorders;

    public bool CanJoinCleanly => Missing.Count == 0;

    public static MeadowModMatch Build(MeadowLobbyMods lobby, MeadowModPolicy policy, CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(current);

        var ids = StringComparer.OrdinalIgnoreCase;
        var required = new HashSet<string>(lobby.Required, ids);
        var active = new HashSet<string>(current.Enabled.Mods.Select(mod => mod.Id), ids);
        var installed = new HashSet<string>(current.Installed.Select(mod => mod.Id), ids);

        // (what this machine calls high impact, plus what the lobby bans) minus what it requires.
        var unwanted = new HashSet<string>(policy.RequiredFor(current), ids);
        unwanted.UnionWith(lobby.Banned);
        unwanted.ExceptWith(required);

        var disable = new List<string>();
        foreach (ModEntry mod in MeadowModPolicy.InLoadOrder(current))
        {
            if (unwanted.Contains(mod.Id) && installed.Contains(mod.Id) && !Holds(disable, mod.Id))
            {
                disable.Add(mod.Id);
            }
        }

        CascadeDependents(disable, current, required);

        var enable = new List<string>();
        var missing = new List<string>();
        foreach (string id in lobby.Required)
        {
            if (active.Contains(id))
            {
                continue;
            }

            if (installed.Contains(id))
            {
                enable.Add(id);
            }
            else
            {
                missing.Add(id);
            }
        }

        return new MeadowModMatch
        {
            Enable = enable,
            Disable = disable,
            Missing = missing,
            Order = lobby.Required,
            Reorders = NeedsReorder(lobby.Required, current),
        };
    }

    // Turning a mod off turns off whatever needed it, which is what the game does rather than
    // leaving a mod loaded against a missing requirement.
    private static void CascadeDependents(
        List<string> disable,
        CurrentMods current,
        HashSet<string> required)
    {
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (ModEntry mod in MeadowModPolicy.InLoadOrder(current))
            {
                if (Holds(disable, mod.Id) || required.Contains(mod.Id))
                {
                    continue;
                }

                foreach (string need in mod.Requirements)
                {
                    if (Holds(disable, need))
                    {
                        disable.Add(mod.Id);
                        grew = true;
                        break;
                    }
                }
            }
        }
    }

    // Rain Meadow accepts the mods being right but the order being wrong only when it is told to
    // ignore it, so a run of required mods out of the lobby's order counts as a change.
    private static bool NeedsReorder(IReadOnlyList<string> required, CurrentMods current)
    {
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in current.Enabled.Mods)
        {
            if (mod.LoadOrder is { } order)
            {
                position.TryAdd(mod.Id, order);
            }
        }

        int previous = int.MinValue;
        foreach (string id in required)
        {
            if (string.Equals(id, MeadowModPolicy.MeadowModId, StringComparison.OrdinalIgnoreCase)
                || !position.TryGetValue(id, out int order))
            {
                continue;
            }

            if (order < previous)
            {
                return true;
            }

            previous = order;
        }

        return false;
    }

    // The mod list this machine should end up with, shaped like any other recorded list so the
    // Mods window previews it and Apply writes it through the checked path every mod change takes.
    // The lobby's mods come first in its own order, and everything still on keeps its order behind
    // them, which is where RainMeadowModManager.CheckMods puts them too.
    public ModListSnapshot WantedList(CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var ids = StringComparer.OrdinalIgnoreCase;
        var turningOff = new HashSet<string>(Disable, ids);
        var required = new HashSet<string>(Order, ids);

        var installed = new Dictionary<string, ModEntry>(ids);
        foreach (ModEntry mod in current.Installed)
        {
            installed.TryAdd(mod.Id, mod);
        }

        var wanted = new List<string>();
        foreach (string id in Order)
        {
            if (!Holds(wanted, id))
            {
                wanted.Add(id);
            }
        }

        foreach (ModEntry mod in MeadowModPolicy.InLoadOrder(current))
        {
            if (!turningOff.Contains(mod.Id) && !required.Contains(mod.Id) && !Holds(wanted, mod.Id))
            {
                wanted.Add(mod.Id);
            }
        }

        var mods = new List<ModEntry>(wanted.Count);
        for (int index = 0; index < wanted.Count; index++)
        {
            installed.TryGetValue(wanted[index], out ModEntry? known);
            mods.Add(new ModEntry
            {
                Id = wanted[index],
                Name = known?.Name ?? wanted[index],
                Version = known?.Version,
                WorkshopId = known?.WorkshopId,
                FolderName = known?.FolderName,
                Origin = known?.Origin ?? "",
                LoadOrder = index,
            });
        }

        return new ModListSnapshot
        {
            ReadTheEnabledList = true,
            CheckedTheInstall = current.Enabled.CheckedTheInstall,
            CheckedTheWorkshop = current.Enabled.CheckedTheWorkshop,
            GameVersion = current.Enabled.GameVersion,
            Mods = mods,
        };
    }

    private static bool Holds(List<string> ids, string id) =>
        ids.Any(held => string.Equals(held, id, StringComparison.OrdinalIgnoreCase));
}
