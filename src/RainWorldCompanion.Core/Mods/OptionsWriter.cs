// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// Rewrites the two records that say which mods are on. The options file also holds every keybind,
/// volume and resolution the player has set, so both edits are spliced by position and every other
/// character in the file is carried across untouched.
/// </summary>
public static class OptionsWriter
{
    /// <param name="enabledIds">Mod ids to turn on, replacing the stored list entirely.</param>
    /// <param name="loadOrder">Positions to set. Ids absent from this keep whatever position the
    /// file already gave them, because the order outlives the mods it names and clearing it would
    /// reshuffle mods this app never touched.</param>
    /// <exception cref="SaveContainerException">The bytes are not an options file this can edit.</exception>
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

    /// <summary>Updates the pairs named and appends the ones not stored yet, leaving the rest in the
    /// order the game wrote them so an unchanged order rewrites no bytes.</summary>
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
