namespace RainWorldCompanion.Core.Saves;

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
