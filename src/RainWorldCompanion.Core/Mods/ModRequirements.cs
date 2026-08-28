// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// What the game turns on with a mod. Remix enables a mod's requirements when you tick it, and a
/// requirement can have requirements of its own, so this walks the whole chain rather than one
/// step of it.
/// </summary>
public static class ModRequirements
{
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Every id reached from <paramref name="modId"/>, in the order the walk meets them, without
    /// the mod itself. A requirement nothing on this machine provides is still named: it is what
    /// the mod asked for, and leaving it out would say it asked for nothing.
    /// </summary>
    /// <remarks>
    /// Cycles are ordinary here rather than a fault: two mods can list each other, and a mod can
    /// list itself. The seen set is what makes that terminate.
    /// </remarks>
    public static IReadOnlyList<string> Closure(string? modId, IEnumerable<ModEntry>? installed)
    {
        var found = new List<string>();

        if (string.IsNullOrWhiteSpace(modId))
        {
            return found;
        }

        var byId = Index(installed);
        var seen = new HashSet<string>(Ids) { modId };
        var queue = new Queue<string>();
        queue.Enqueue(modId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // A mod nothing on disk provides has no modinfo.json to read, so the chain stops at it
            // rather than being guessed at.
            if (!byId.TryGetValue(current, out ModEntry? mod))
            {
                continue;
            }

            foreach (var required in mod.Requirements)
            {
                if (string.IsNullOrWhiteSpace(required) || !seen.Add(required))
                {
                    continue;
                }

                found.Add(required);
                queue.Enqueue(required);
            }
        }

        return found;
    }

    /// <summary>
    /// Which of <paramref name="wanted"/> require which. Used to say what would break before
    /// turning something off, and it names only the mods actually being left on.
    /// </summary>
    public static IReadOnlyList<string> WhatNeeds(
        string? modId,
        IEnumerable<string>? wanted,
        IEnumerable<ModEntry>? installed)
    {
        var needing = new List<string>();

        if (string.IsNullOrWhiteSpace(modId) || wanted is null)
        {
            return needing;
        }

        var byId = Index(installed);

        foreach (var candidate in wanted)
        {
            if (Ids.Equals(candidate, modId) || !byId.ContainsKey(candidate))
            {
                continue;
            }

            if (Closure(candidate, installed).Any(required => Ids.Equals(required, modId)))
            {
                needing.Add(candidate);
            }
        }

        return needing;
    }

    private static Dictionary<string, ModEntry> Index(IEnumerable<ModEntry>? installed)
    {
        var byId = new Dictionary<string, ModEntry>(Ids);

        if (installed is null)
        {
            return byId;
        }

        foreach (var mod in installed)
        {
            if (mod is not null && !string.IsNullOrWhiteSpace(mod.Id))
            {
                byId[mod.Id] = mod;
            }
        }

        return byId;
    }
}
