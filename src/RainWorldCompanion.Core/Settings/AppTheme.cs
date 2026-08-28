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

    public static AppTheme Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        DarkText => AppTheme.Dark,
        _ => AppTheme.Light,
    };

    /// <summary>
    /// Text rather than the enum's number, for the same reason as
    /// <see cref="RainWorldCompanion.Core.Updates.UpdateChannels.ToStorageString"/>.
    /// </summary>
    public static string ToStorageString(this AppTheme theme) => theme switch
    {
        AppTheme.Dark => DarkText,
        _ => LightText,
    };

    public static AppTheme Other(this AppTheme theme) =>
        theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
}
