using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

public class DurableFileTests
{
    [Fact]
    public void Replacing_a_file_leaves_the_new_text_and_no_temporary()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("manifest.json", "old");

        DurableFile.ReplaceText(path, "{\"new\": true}");

        Assert.Equal("{\"new\": true}", File.ReadAllText(path));
        Assert.Equal(["manifest.json"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void A_temporary_left_by_an_interrupted_write_does_not_block_the_next_one()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText("manifest.json", "old");
        dir.WriteText("manifest.json.tmp", "");

        DurableFile.ReplaceText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Copy_never_overwrites_an_existing_file()
    {
        using var dir = new TempDirectory();
        var source = dir.WriteText("settings.json", "broken");
        var destination = dir.WriteText("settings.json.corrupt-1", "earlier");

        Assert.Throws<IOException>(() => DurableFile.Copy(source, destination));
        Assert.Equal("earlier", File.ReadAllText(destination));
    }
}
