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

        // Occurrence zero throughout, because InputSetup proves keys repeat here and OptionsFile
        // reads the first of them.
        string edited = DelimitedFields.Options.SetValue(
            blob,
            OptionsFile.EnabledModsKey,
            string.Join(OptionsFile.ListSeparator, enabledIds));

        string storedOrder = DelimitedFields.Options.GetValue(blob, OptionsFile.ModLoadOrderKey) ?? "";
        string newOrder = MergeLoadOrder(storedOrder, loadOrder);

        if (!string.Equals(storedOrder, newOrder, StringComparison.Ordinal))
        {
            edited = DelimitedFields.Options.SetValue(edited, OptionsFile.ModLoadOrderKey, newOrder);
        }

        return container.WithValue(OptionsFile.ContainerKey, edited).ToBytes();
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
