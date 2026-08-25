namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// What Inno Setup's exit codes mean, in words a person can act on.
///
/// The app watches the installer briefly after starting it, and a code arriving inside that window
/// means Setup gave up before it began. Reporting the bare number leaves the user with nothing to
/// do about it, and these are documented and stable, so they are worth spelling out. Codes are
/// from the Inno Setup help, "Setup Exit Codes".
/// </summary>
public static class InstallerExitCodes
{
    /// <summary>
    /// The sentence for a code, or a plain fallback. Never empty, because the caller is building
    /// a message the user will read.
    /// </summary>
    public static string Describe(int code) => code switch
    {
        0 => "The installer finished.",
        1 => "The installer could not start up.",
        2 => "The installation was cancelled before it began.",
        3 => "The installer hit a fatal error while getting ready, which usually means the "
             + "machine is out of memory or Windows resources.",
        4 => "The installer hit a fatal error partway through installing.",
        5 => "The installation was cancelled while it was running.",
        6 => "The installer was terminated from outside.",
        7 => "The installer decided it could not go ahead, and will have said why in a window of "
             + "its own.",
        8 => "The installer decided it could not go ahead until the machine is restarted.",
        _ => "The installer stopped for a reason it did not name.",
    };

    /// <summary>
    /// Whether this code means the install completed. Only zero does. Everything else leaves the
    /// copy already on disk as the one still worth running, which is why the app stays open for
    /// all of them.
    /// </summary>
    public static bool IsSuccess(int code) => code == 0;
}
