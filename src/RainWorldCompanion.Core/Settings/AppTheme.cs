namespace RainWorldCompanion.Core.Settings;

public enum AppTheme
{
    Light,
    Dark,
}

public static class AppThemes
{
    private const string LightText = "light";
    private const string DarkText = "dark";

    /// <summary>Dark is the default, so only the word "light" turns it off.</summary>
    public static AppTheme Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        LightText => AppTheme.Light,
        _ => AppTheme.Dark,
    };

    /// <summary>
    /// Text rather than the enum's number, for the same reason as
    /// <see cref="RainWorldCompanion.Core.Updates.UpdateChannels.ToStorageString"/>.
    /// </summary>
    public static string ToStorageString(this AppTheme theme) => theme switch
    {
        AppTheme.Light => LightText,
        _ => DarkText,
    };

    public static AppTheme Other(this AppTheme theme) =>
        theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
}
