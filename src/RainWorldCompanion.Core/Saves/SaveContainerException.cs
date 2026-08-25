namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// Raised when a save container file cannot be read or parsed.
/// </summary>
public sealed class SaveContainerException : Exception
{
    public SaveContainerException(string message)
        : base(message)
    {
    }

    public SaveContainerException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
