namespace RainWorldCompanion.Core.Updates;

/// <summary>Codes are from the Inno Setup help, "Setup Exit Codes".</summary>
public static class InstallerExitCodes
{
    /// <summary>Never empty: the caller is building a message the user will read.</summary>
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

    public static bool IsSuccess(int code) => code == 0;
}
