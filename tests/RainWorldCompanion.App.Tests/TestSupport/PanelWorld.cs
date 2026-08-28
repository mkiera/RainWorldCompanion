using System.IO;
using System.Text.Json;
using System.Windows.Media;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>Frozen, so the tests can build view models on whatever thread xunit hands them.</summary>
internal sealed class FakeIcons : ISlugcatIconProvider
{
    private static readonly ImageSource Blank = CreateBlank();

    public ImageSource GetIcon(string? slugcatId) => Blank;

    private static ImageSource CreateBlank()
    {
        var image = new DrawingImage(new GeometryDrawing());
        image.Freeze();
        return image;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(30);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(30);
            }
        }
    }
}

/// <summary>
/// These helpers exist so a test can ask one kind a question without the other two having to
/// be on disk.
/// </summary>
internal static class Panels
{
    public const string AppVersion = "1.0.0-test";

    public static SlotMetadata Slot(
        int number,
        SaveRealm realm = SaveRealm.Local,
        string? fileName = null,
        int campaigns = 1,
        int? recordCount = 4) =>
        new()
        {
            Slot = number,
            FileName = fileName ?? new SaveSlotRef(realm, number).FileName,
            Realm = realm,
            ChecksumValid = true,
            RecordCount = recordCount,
            Campaigns = Enumerable.Range(0, campaigns)
                .Select(index => new CampaignSummary { SlugcatId = "White", CycleNum = 10 + index })
                .ToList(),
        };

    public static SnapshotDetailViewModel Live(params SlotMetadata[] slots) =>
        SnapshotDetailViewModel.ForLive(slots, @"C:\saves", 1024, slots.Length, null, new FakeIcons());

    /// <summary>
    /// A snapshot is loaded off disk, so this writes a folder holding just the manifest, which is
    /// all the panel reads.
    /// </summary>
    public static SnapshotDetailViewModel Backup(TempDirectory root, params SlotMetadata[] slots)
        => Backup(root, null, slots);

    /// <param name="liveSlots">What is in the save folder now, for the differs-from-live chips.</param>
    public static SnapshotDetailViewModel Backup(
        TempDirectory root, IReadOnlyList<SlotMetadata>? liveSlots, params SlotMetadata[] slots)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root.Path, "2026-01-01_00-00-00")).FullName;

        var manifest = new BackupManifest
        {
            AppVersion = AppVersion,
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Label = "a backup",
            Kind = BackupKind.Manual,
        };
        manifest.Slots.AddRange(slots);

        File.WriteAllText(
            Path.Combine(directory, BackupSnapshot.ManifestFileName),
            JsonSerializer.Serialize(manifest, BackupJson.Options));

        var item = new BackupItemViewModel(BackupSnapshot.Load(directory), new FakeIcons());
        return SnapshotDetailViewModel.ForBackup(item, null, new FakeIcons(), liveSlots: liveSlots);
    }

    /// <param name="folderNameSuffix">
    /// Keeps two entries in one root apart, the way the real timestamp suffix does.
    /// </param>
    public static LibraryEntryViewModel EntryRow(
        TempDirectory root,
        SlotMetadata metadata,
        string name = "a stored save",
        string? sourceFileName = null,
        string folderNameSuffix = "")
    {
        var folderName = "2026-01-01_00-00-00" + (folderNameSuffix.Length == 0 ? "" : "_" + folderNameSuffix);
        var directory = Directory.CreateDirectory(Path.Combine(root.Path, folderName)).FullName;

        // SaveLibrary parses the stored copy, so the metadata a real entry carries names the
        // library's own storage file rather than the container it came from.
        var stored = new SlotMetadata
        {
            Slot = metadata.Slot,
            FileName = LibraryEntry.SaveFileName,
            Realm = metadata.Realm,
            ChecksumValid = metadata.ChecksumValid,
            RecordCount = metadata.RecordCount,
            Campaigns = metadata.Campaigns,
            ParseError = metadata.ParseError,
        };

        var manifest = new LibraryManifest
        {
            Name = name,
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AppVersion = AppVersion,
            SourceFileName = sourceFileName ?? metadata.FileName,
            SourceRealm = metadata.Realm,
            SourceSlot = metadata.Slot,
            SizeBytes = 4096,
            Sha256 = new string('0', 64),
            Metadata = stored,
        };

        File.WriteAllBytes(Path.Combine(directory, LibraryEntry.SaveFileName), new byte[] { 0xEF, 0xBB, 0xBF });
        File.WriteAllText(
            Path.Combine(directory, LibraryEntry.ManifestFileName),
            JsonSerializer.Serialize(manifest, BackupJson.Options));

        return new LibraryEntryViewModel(LibraryEntry.Load(directory), new FakeIcons(), @"C:\saves");
    }

    public static SnapshotDetailViewModel Entry(
        TempDirectory root,
        SlotMetadata metadata,
        string name = "a stored save",
        string? sourceFileName = null,
        string folderNameSuffix = "",
        IReadOnlyList<SlotMetadata>? liveSlots = null) =>
        SnapshotDetailViewModel.ForLibraryEntry(
            EntryRow(root, metadata, name, sourceFileName, folderNameSuffix),
            new FakeIcons(),
            liveSlots: liveSlots);
}
