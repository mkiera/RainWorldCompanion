using System.IO.Compression;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Tests;

namespace RainWorldCompanion.Core.Tests;

public class RainWorldLogBundleTests
{
    private static readonly TimeProvider Clock = new FixedClock(
        new DateTimeOffset(2026, 9, 5, 14, 23, 45, TimeSpan.Zero));

    [Fact]
    public void Both_Rain_World_logs_are_bundled_with_their_contents()
    {
        using var files = new TempDirectory("rain-world-log-bundle");
        var install = files.CreateSubdirectory("Rain World");
        var downloads = files.CreateSubdirectory("Downloads");
        File.WriteAllText(Path.Combine(install, "consoleLog.txt"), "console output");
        File.WriteAllText(Path.Combine(install, "exceptionLog.txt"), "exception output");

        RainWorldLogBundleResult result = RainWorldLogBundle.Create(install, downloads, Clock);

        Assert.Equal(
            Path.Combine(downloads, "Rain World logs 2026-09-05 14-23-45.zip"),
            result.ArchivePath);
        Assert.Equal(["consoleLog.txt", "exceptionLog.txt"], result.IncludedFileNames);

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(["consoleLog.txt", "exceptionLog.txt"], archive.Entries.Select(entry => entry.FullName));
        Assert.Equal("console output", Read(archive.GetEntry("consoleLog.txt")!));
        Assert.Equal("exception output", Read(archive.GetEntry("exceptionLog.txt")!));
    }

    [Fact]
    public void A_single_existing_log_still_produces_a_bundle()
    {
        using var files = new TempDirectory("rain-world-log-bundle");
        var install = files.CreateSubdirectory("Rain World");
        var downloads = files.Resolve("Downloads");
        File.WriteAllText(Path.Combine(install, "consoleLog.txt"), "console output");

        RainWorldLogBundleResult result = RainWorldLogBundle.Create(install, downloads, Clock);

        Assert.Equal(["consoleLog.txt"], result.IncludedFileNames);
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal("console output", Read(Assert.Single(archive.Entries)));
    }

    [Fact]
    public void An_existing_bundle_is_not_overwritten()
    {
        using var files = new TempDirectory("rain-world-log-bundle");
        var install = files.CreateSubdirectory("Rain World");
        var downloads = files.CreateSubdirectory("Downloads");
        File.WriteAllText(Path.Combine(install, "consoleLog.txt"), "console output");
        var existing = Path.Combine(downloads, "Rain World logs 2026-09-05 14-23-45.zip");
        File.WriteAllText(existing, "keep me");

        RainWorldLogBundleResult result = RainWorldLogBundle.Create(install, downloads, Clock);

        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Equal(
            Path.Combine(downloads, "Rain World logs 2026-09-05 14-23-45 (2).zip"),
            result.ArchivePath);
    }

    [Fact]
    public void Missing_logs_report_the_install_folder_and_write_nothing()
    {
        using var files = new TempDirectory("rain-world-log-bundle");
        var install = files.CreateSubdirectory("Rain World");
        var downloads = files.Resolve("Downloads");

        var exception = Assert.Throws<FileNotFoundException>(() =>
            RainWorldLogBundle.Create(install, downloads, Clock));

        Assert.Contains("consoleLog.txt", exception.Message);
        Assert.Contains("exceptionLog.txt", exception.Message);
        Assert.Contains(install, exception.Message);
        Assert.False(Directory.Exists(downloads));
    }

    [Fact]
    public void A_log_held_open_for_writing_can_be_bundled()
    {
        using var files = new TempDirectory("rain-world-log-bundle");
        var install = files.CreateSubdirectory("Rain World");
        var downloads = files.CreateSubdirectory("Downloads");
        var console = Path.Combine(install, "consoleLog.txt");
        File.WriteAllText(console, "live output");
        using var writer = new FileStream(
            console,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        RainWorldLogBundleResult result = RainWorldLogBundle.Create(install, downloads, Clock);

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal("live output", Read(Assert.Single(archive.Entries)));
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
