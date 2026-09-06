using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Saves;

public sealed record DenMapAvailability(bool Available, string Reason)
{
    public static DenMapAvailability Check(string slugcatId, ExpansionPresence expansions, OptionsRead options)
    {
        if (!new[] { "White", "Yellow", "Red", "Gourmand" }.Contains(slugcatId, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, "The Downpour map supports Survivor, Monk, Hunter, and Gourmand.");
        }

        if (!expansions.CheckedTheInstall)
        {
            return new(false, "Set a readable Rain World installation folder in Settings to use the map.");
        }

        if (!expansions.Downpour)
        {
            return new(false, "The map requires Downpour to be installed and enabled.");
        }

        if (!options.Read)
        {
            return new(false, "The game's options could not be read, so Downpour's enabled state is unknown.");
        }

        if (!options.EnabledModIds.Contains(ExpansionDetector.DownpourModId, StringComparer.OrdinalIgnoreCase))
        {
            return new(false, "Enable More Slugcats Expansion in Rain World to use the Downpour map.");
        }

        return new(true, "Choose a den on the Downpour world map.");
    }
}
