using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The folder downloaded installers land in.
///
/// Its one real job is the containment check, which is the guard between "a file this app
/// downloaded" and "a file this app executes". Everything else here is about not letting a name
/// that arrived over the network decide where a byte gets written.
/// </summary>
public class UpdatesFolderTests
{
    [Fact]
    public void The_folder_sits_under_the_settings_folder_so_the_uninstaller_can_name_it()
    {
        // Not the temp directory: a cleaner emptying temp between the download and the launch
        // would take the installer with it, and an uninstaller cannot name a temp path.
        Assert.Equal(
            Path.Combine(SettingsRoot(), "updates"),
            UpdatesFolder.Location);
    }

    [Theory]
    [InlineData("RainWorldCompanion-Setup.exe")]
    [InlineData("rainworldcompanion-setup.exe")]
    public void A_plainly_named_asset_gets_a_path_inside_the_folder(string name)
    {
        var path = UpdatesFolder.PathFor(name);

        Assert.NotNull(path);
        Assert.Equal(Path.Combine(UpdatesFolder.Location, name), path);
    }

    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("C:\\Windows\\System32\\evil.exe")]
    [InlineData("sub/dir/setup.exe")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_that_could_steer_the_write_gets_no_path_at_all(string? name)
    {
        // Refused rather than sanitised. A name that needed cleaning up was not the name of
        // anything this app published, so there is nothing worth downloading behind it.
        Assert.Null(UpdatesFolder.PathFor(name));
    }

    [Fact]
    public void A_file_in_the_folder_is_recognised_as_being_in_it()
    {
        // The file is really created rather than named. Contains resolves both sides through the
        // filesystem, and a path that does not exist falls back to the textual form, so a test
        // that only named one would be comparing a resolved folder against an unresolved file and
        // would answer differently depending on whether the folder happened to exist.
        UpdatesFolder.Ensure();
        var inside = Path.Combine(UpdatesFolder.Location, "contains-probe.tmp");
        File.WriteAllText(inside, "");

        try
        {
            Assert.True(UpdatesFolder.Contains(inside));
        }
        finally
        {
            File.Delete(inside);
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_outside_the_folder_is_not(string? candidate)
    {
        Assert.False(UpdatesFolder.Contains(candidate));
    }

    [Fact]
    public void The_folder_itself_does_not_count_as_being_inside_it()
    {
        // Contains answers "is this a file I may run", and the folder is not a file.
        Assert.False(UpdatesFolder.Contains(UpdatesFolder.Location));
    }

    [Fact]
    public void A_sibling_folder_whose_name_starts_the_same_is_not_inside_it()
    {
        Assert.False(UpdatesFolder.Contains(UpdatesFolder.Location + "-old"));
    }

    [Fact]
    public void Clearing_a_folder_that_was_never_created_is_not_a_failure()
    {
        // Runs at every startup, including the first one on a machine that has never updated.
        var record = Record.Exception(UpdatesFolder.Clear);

        Assert.Null(record);
    }

    private static string SettingsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldCompanion");
}
