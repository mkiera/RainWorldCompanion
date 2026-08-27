// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Windows.Media;

namespace RainWorldCompanion.Services;

public interface ISlugcatIconProvider
{
    /// <summary>Never null. The result is frozen and can be handed to any thread.</summary>
    ImageSource GetIcon(string? slugcatId);
}
