// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// The one wording for "how many campaigns", used by the live save card, the backup rows and the
/// detail header.
///
/// A folder counts its two realms separately because it shows them separately: the slot sections
/// list the local saves and the Rain Meadow section lists the online ones. A single total would
/// head a list that does not add up to it, and counting them differently in different places puts
/// two numbers for the same folder on screen at once. One library save is a single file with one
/// section, so it passes zero for the online half whichever realm it came from.
/// </summary>
internal static class CampaignCount
{
    /// <summary>"11 campaigns", or "11 campaigns +1 online" when the online saves hold some.</summary>
    public static string Describe(int local, int online)
    {
        string text = local switch
        {
            0 => "no campaigns",
            1 => "1 campaign",
            _ => local.ToString(CultureInfo.InvariantCulture) + " campaigns",
        };

        return online == 0
            ? text
            : text + " +" + online.ToString(CultureInfo.InvariantCulture) + " online";
    }
}
