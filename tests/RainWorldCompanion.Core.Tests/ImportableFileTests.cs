using RainWorldCompanion.Core.Library;

namespace RainWorldCompanion.Tests;

public class ImportableFileTests
{
    [Theory]
    [InlineData(@"C:\out\ironclaw.rwsave", ImportableKind.Save)]
    [InlineData(@"C:\out\Survivor.rwcampaign", ImportableKind.Save)]
    [InlineData(@"C:\out\IRONCLAW.RWSAVE", ImportableKind.Save)]
    [InlineData(@"C:\Games\Rain World\sav", ImportableKind.Save)]
    [InlineData(@"C:\Games\Rain World\sav3", ImportableKind.Save)]
    [InlineData(@"C:\Games\Rain World\online_sav2", ImportableKind.Save)]
    [InlineData(@"C:\out\my mods.rwconfigs", ImportableKind.ModSettings)]
    [InlineData(@"C:\out\Rain World mods.rwmods", ImportableKind.ModList)]
    [InlineData("list.RWMODS", ImportableKind.ModList)]
    public void A_file_the_app_writes_is_sorted_by_its_name(string path, ImportableKind expected)
    {
        Assert.Equal(expected, ImportableFile.Classify(path));
    }

    [Theory]
    [InlineData(@"C:\out\notes.txt")]
    [InlineData(@"C:\out\list.json")]
    [InlineData(@"C:\Games\Rain World\sav - Copy")]
    [InlineData(@"C:\Games\Rain World\sav4")]
    [InlineData(@"C:\out\archive.rwsave.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_unknown(string? path)
    {
        Assert.Equal(ImportableKind.Unknown, ImportableFile.Classify(path));
    }
}
