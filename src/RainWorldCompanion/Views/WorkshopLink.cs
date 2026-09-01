using System.Diagnostics;

namespace RainWorldCompanion.Views;

/// <summary>
/// Opens a Steam page where Steam can take it. The Subscribe button only does anything for somebody
/// signed in, and the client always is, while a browser may not be.
/// </summary>
internal static class WorkshopLink
{
    /// <summary>Hands any web address to the Steam client's own browser.</summary>
    public const string SteamOpenPrefix = "steam://openurl/";

    /// <summary>
    /// Tries Steam first and falls back to the web address. A machine without Steam has nothing
    /// registered for the protocol, so starting it throws and the browser gets it instead.
    /// </summary>
    /// <param name="report">Told why nothing opened, when neither did.</param>
    public static void Open(string url, Action<string>? report = null)
    {
        if (url.Length == 0 || TryStart(UrlForSteam(url)))
        {
            return;
        }

        if (!TryStart(url, out string problem))
        {
            report?.Invoke(problem);
        }
    }

    internal static string UrlForSteam(string url) => url.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)
        ? url
        : SteamOpenPrefix + url;

    private static bool TryStart(string url) => TryStart(url, out _);

    private static bool TryStart(string url, out string problem)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            problem = "";
            return true;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
    }
}
