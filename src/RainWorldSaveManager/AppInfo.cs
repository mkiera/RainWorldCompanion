namespace RainWorldSaveManager;

/// <summary>
/// What the app calls itself on screen.
///
/// One constant, because the name was previously written out at every message box, the window
/// title and the installer, and renaming it meant finding all of them. The installer script and
/// Directory.Build.props hold their own copies, since neither can read a C# constant, and those
/// two are the only other places it appears.
///
/// Note this is the display name only. The assembly, the namespaces, the settings folder and the
/// single-instance mutex are all still called RainWorldSaveManager: they identify the installed
/// thing rather than describe it, and renaming those would strand the settings file and the
/// backups it points at for no visible gain.
/// </summary>
internal static class AppInfo
{
    /// <summary>
    /// The product name. Shown in the title bar, every message box caption, the Start menu, and
    /// Add/Remove Programs.
    /// </summary>
    public const string DisplayName = "RainWorld Companion";
}
