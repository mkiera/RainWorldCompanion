// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// A folder counts its two realms separately because it shows them separately. One library save is
/// a single file with one section, so it passes zero for the online half whichever realm it is.
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
