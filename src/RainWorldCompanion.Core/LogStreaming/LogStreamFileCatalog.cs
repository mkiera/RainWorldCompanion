namespace RainWorldCompanion.Core.LogStreaming;

public sealed record LogStreamFile(string Id, string RelativePath, bool IsGenerated = false);

public static class LogStreamFileCatalog
{
    private static readonly LogStreamFile[] SourceFiles =
    [
        new("consoleLog.txt", "consoleLog.txt"),
        new("exceptionLog.txt", "exceptionLog.txt"),
        new("BepInEx/LogOutput.log", Path.Combine("BepInEx", "LogOutput.log")),
    ];

    private static readonly LogStreamFile[] GeneratedFiles =
    [
        new("Companion/events.jsonl", Path.Combine("Companion", "events.jsonl"), true),
        new("Companion/deep-trace.jsonl", Path.Combine("Companion", "deep-trace.jsonl"), true),
    ];

    private static readonly LogStreamFile[] StreamFiles = [.. SourceFiles, .. GeneratedFiles];
    private static readonly IReadOnlyList<LogStreamFile> ReadOnlySourceFiles = Array.AsReadOnly(SourceFiles);
    private static readonly IReadOnlyList<LogStreamFile> ReadOnlyGeneratedFiles = Array.AsReadOnly(GeneratedFiles);

    public static IReadOnlyList<LogStreamFile> Files => ReadOnlySourceFiles;
    public static IReadOnlyList<LogStreamFile> Generated => ReadOnlyGeneratedFiles;

    public static bool TryGet(string? fileId, out LogStreamFile file)
    {
        file = StreamFiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, fileId, StringComparison.Ordinal))!;
        return file is not null;
    }

    public static string ResolveSourcePath(string installRoot, string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        var file = SourceFiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, fileId, StringComparison.Ordinal));
        if (file is null)
        {
            throw new ArgumentException("The file is not a source log.", nameof(fileId));
        }

        return Path.Combine(Path.GetFullPath(installRoot), file.RelativePath);
    }
}
