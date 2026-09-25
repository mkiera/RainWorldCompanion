namespace RainWorldCompanion.Core.LogStreaming;

internal static class LogStreamValidation
{
    public static bool IsToken(string? value, int maximumLength = 128)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && value.All(character =>
                character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or ':');

    public static void RequireToken(string? value, string parameterName, int maximumLength = 128)
    {
        if (!IsToken(value, maximumLength))
        {
            throw new ArgumentException(
                "Identifiers may contain only ASCII letters, digits, periods, colons, dashes, and underscores.",
                parameterName);
        }
    }
}
