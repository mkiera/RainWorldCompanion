using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Saves;

public sealed record DenMapAvailability(bool Available, string Reason, bool DownpourEnabled = false)
{
    public static DenMapAvailability Check(string slugcatId, ExpansionPresence expansions, OptionsRead options)
    {
        if (!DenWorldCatalog.Timelines.Contains(slugcatId, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, "There is no supplied map for this campaign. Type a den below.");
        }

        if (!expansions.CheckedTheInstall)
        {
            return new(false, "Set a readable Rain World installation folder in Settings to use the map.");
        }

        if (expansions.Downpour && !options.Read)
        {
            return new(false, "The game's options could not be read, so Downpour's enabled state is unknown.");
        }

        bool downpourEnabled = expansions.Downpour && options.EnabledModIds.Contains(
            ExpansionDetector.DownpourModId, StringComparer.OrdinalIgnoreCase);
        if (!downpourEnabled && !new[] { "White", "Yellow", "Red" }.Contains(slugcatId, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, "This campaign's map requires More Slugcats Expansion to be installed and enabled.");
        }

        return new(true, "Choose a den on the world map.", downpourEnabled);
    }
}
