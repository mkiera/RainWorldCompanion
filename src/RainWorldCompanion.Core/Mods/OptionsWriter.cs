// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Mods;

public static class OptionsWriter
{
    public static byte[] Rewrite(
        byte[] optionsBytes,
        IReadOnlyList<string> enabledIds,
        IReadOnlyDictionary<string, int> loadOrder)
    {
        ArgumentNullException.ThrowIfNull(enabledIds);
        ArgumentNullException.ThrowIfNull(loadOrder);

        ContainerText container = ContainerText.Load(optionsBytes);
        string blob = container.GetValue(OptionsFile.ContainerKey);

        string storedEnabled = DelimitedFields.Options.GetValue(blob, OptionsFile.EnabledModsKey) ?? "";

        // Occurrence zero throughout, because InputSetup proves keys repeat here and OptionsFile
        // reads the first of them.
        string edited = DelimitedFields.Options.SetValue(
            blob,
            OptionsFile.EnabledModsKey,
            string.Join(OptionsFile.ListSeparator, KeepStoredOrder(storedEnabled, enabledIds)));

        string storedOrder = DelimitedFields.Options.GetValue(blob, OptionsFile.ModLoadOrderKey) ?? "";
        string newOrder = MergeLoadOrder(storedOrder, loadOrder);

        if (!string.Equals(storedOrder, newOrder, StringComparison.Ordinal))
        {
            edited = DelimitedFields.Options.SetValue(edited, OptionsFile.ModLoadOrderKey, newOrder);
        }

        return container.WithValue(OptionsFile.ContainerKey, edited).ToBytes();
    }

    // The game writes this list in its own order, which is not load order. Keeping that order means
    // a rewrite that turns one mod on moves one line rather than all of them.
    private static List<string> KeepStoredOrder(string stored, IReadOnlyList<string> enabledIds)
    {
        var wanted = new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in stored.Split(OptionsFile.ListSeparator, StringSplitOptions.None))
        {
            string id = raw.Trim();
            if (id.Length > 0 && wanted.Contains(id) && seen.Add(id))
            {
                kept.Add(id);
            }
        }

        foreach (string id in enabledIds)
        {
            if (id.Trim().Length > 0 && seen.Add(id))
            {
                kept.Add(id);
            }
        }

        return kept;
    }

    private static string MergeLoadOrder(string stored, IReadOnlyDictionary<string, int> loadOrder)
    {
        var pending = new Dictionary<string, int>(loadOrder, StringComparer.OrdinalIgnoreCase);
        var pairs = new List<string>();

        foreach (string raw in stored.Split(OptionsFile.ListSeparator, StringSplitOptions.None))
        {
            int split = raw.IndexOf(OptionsFile.PairSeparator, StringComparison.Ordinal);
            if (split < 0)
            {
                // A pair that will not split is left exactly as found rather than dropped.
                if (raw.Length > 0)
                {
                    pairs.Add(raw);
                }

                continue;
            }

            string id = raw[..split].Trim();

            if (id.Length > 0 && pending.Remove(id, out int position))
            {
                pairs.Add(id + OptionsFile.PairSeparator + position.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            pairs.Add(raw);
        }

        foreach ((string id, int position) in pending.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            pairs.Add(id + OptionsFile.PairSeparator + position.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(OptionsFile.ListSeparator, pairs);
    }
}
