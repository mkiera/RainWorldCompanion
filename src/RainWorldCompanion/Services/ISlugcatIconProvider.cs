// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced Core assembly, so a using written inside the namespace body risks
// binding "System" to that namespace instead of the BCL root.
using System.Windows.Media;

namespace RainWorldCompanion.Services;

/// <summary>
/// Supplies the picture shown next to a campaign.
/// </summary>
public interface ISlugcatIconProvider
{
    /// <summary>
    /// The icon for a slugcat id. Never returns null, so a binding never has to null check and a
    /// missing portrait shows a drawn stand-in instead of an empty gap. The result is frozen and
    /// can be handed to any thread.
    /// </summary>
    ImageSource GetIcon(string? slugcatId);
}
