namespace RainWorldCompanion;

/// <summary>
/// What the app calls itself on screen.
///
/// One constant, because the name was previously written out at every message box, the window
/// title and the installer, and renaming it meant finding all of them. The installer script and
/// Directory.Build.props hold their own copies, since neither can read a C# constant, and those
/// two are the only other places it appears.
///
/// The spaced form here is the display name only. Everything that identifies rather than describes
/// spells it RainWorldCompanion with no space: the assembly, the namespaces, the settings folder
/// and the single-instance mutex.
/// </summary>
internal static class AppInfo
{
    /// <summary>
    /// The product name. Shown in the title bar, every message box caption, the Start menu, and
    /// Add/Remove Programs.
    /// </summary>
    public const string DisplayName = "RainWorld Companion";
}
