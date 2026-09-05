using System.Globalization;
using System.IO.Compression;

namespace RainWorldCompanion.Core.System;

public sealed record RainWorldLogBundleResult(
    string ArchivePath,
    IReadOnlyList<string> IncludedFileNames);

public static class RainWorldLogBundle
{
    private static readonly (string ArchiveName, string RelativePath)[] LogFiles =
    [
        ("consoleLog.txt", "consoleLog.txt"),
        ("exceptionLog.txt", "exceptionLog.txt"),
        ("BepInEx/LogOutput.log", Path.Combine("BepInEx", "LogOutput.log")),
    ];

    public static RainWorldLogBundleResult Create(
        string installPath,
        string destinationDirectory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var installRoot = Path.GetFullPath(installPath.Trim());
        var sources = LogFiles
            .Select(log => new { Name = log.ArchiveName, Path = Path.Combine(installRoot, log.RelativePath) })
            .Where(source => File.Exists(source.Path))
            .ToList();

        if (sources.Count == 0)
        {
            throw new FileNotFoundException(
                "No consoleLog.txt, exceptionLog.txt, or BepInEx/LogOutput.log was found in the Rain World install folder: "
                + installRoot);
        }

        var destinationRoot = Path.GetFullPath(destinationDirectory.Trim());
        Directory.CreateDirectory(destinationRoot);

        var temporaryPath = Path.Combine(
            destinationRoot,
            ".rain-world-logs-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            WriteArchive(temporaryPath, sources.Select(source => (source.Name, source.Path)));

            var timestamp = (timeProvider ?? TimeProvider.System)
                .GetLocalNow()
                .ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            var stem = "Rain World logs " + timestamp;
            var archivePath = MoveToAvailableName(temporaryPath, destinationRoot, stem);

            return new RainWorldLogBundleResult(
                archivePath,
                sources.Select(source => source.Name).ToArray());
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void WriteArchive(
        string archivePath,
        IEnumerable<(string Name, string Path)> sources)
    {
        using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);

        foreach (var source in sources)
        {
            var entry = archive.CreateEntry(source.Name, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var sourceStream = new FileStream(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            sourceStream.CopyTo(entryStream);
        }
    }

    private static string MoveToAvailableName(string temporaryPath, string destinationRoot, string stem)
    {
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? "" : " (" + number + ")";
            var candidate = Path.Combine(destinationRoot, stem + suffix + ".zip");

            try
            {
                File.Move(temporaryPath, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
