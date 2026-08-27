using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Mod settings built by hand, for tests about what is recorded and offered rather than about how
/// a folder is read. Reading a real folder is covered by ModConfigReaderTests.
/// </summary>
internal static class ModConfigs
{
    /// <summary>A settings file for one mod, in the shape Remix writes.</summary>
    public const string SampleConfig =
        "## Config file for Devourment (devourment)\n" +
        "## Mod Version: 0.1.11-ea\n" +
        "\n" +
        "# Setting type: Boolean\n" +
        "# Default value: false\n" +
        "DvrmentPredatorMode = true\n";

    /// <summary>The trap: gameplay settings and a window size in one ordinary settings file.</summary>
    public const string ConfigWithDisplaySettings =
        "## Config file for SBCameraScroll (SBCameraScroll)\n" +
        "\n" +
        "customResolution = 1920x1080\n" +
        "resolution = Default\n" +
        "fullScreenEffects = true\n" +
        "scrollSpeed = 0.4\n";

    public static ModConfigFile File(string relativePath, string modId, long sizeBytes = 64)
        => new()
        {
            RelativePath = relativePath,
            ModId = modId,
            SizeBytes = sizeBytes,
            Sha256 = new string('a', 64),
        };

    /// <summary>A set that was read in full, which is the ordinary case.</summary>
    public static ModConfigSet Set(params ModConfigFile[] files)
        => new() { ReadTheFolder = true, Files = files.ToList() };

    /// <summary>A read that found nothing, which must never be shown as "there were none".</summary>
    public static ModConfigSet CouldNotLook()
        => new() { ReadTheFolder = false, Note = "The save folder is not known." };

    /// <summary>Writes a realistic ModConfigs tree into a folder standing in for a save folder.</summary>
    public static void Populate(TempDirectory directory, string prefix = "")
    {
        string at(string relative) => prefix.Length == 0 ? relative : prefix + "\\" + relative;

        directory.WriteText(at(@"ModConfigs\devourment.txt"), SampleConfig);
        directory.WriteText(at(@"ModConfigs\moreslugcats.txt"), "SomeOtherMod = 1\n");
        directory.WriteText(at(@"ModConfigs\SBCameraScroll.txt"), ConfigWithDisplaySettings);
        directory.WriteText(at(@"ModConfigs\DvrmentConfs\current.json"), "{\"preset\":\"default\"}");
        directory.WriteText(at(@"ModConfigs\DvrmentConfs\Preset-kieracustom.txt"), "Lizard,1,2,3\n");

        // Present, and none of it travels.
        directory.WriteText(at(@"ModConfigs\steam_autocloud.vdf"), "\"RootPaths\"\n{\n}\n");
        directory.WriteText(at(@"ModConfigs\willowwisp.bellyplus.json"), "{\"bpDifficulty\":-3.323608}");
        directory.WriteText(at(@"ModConfigs\MapOptions\cache.json"), "{\"zoom\":2}");
    }

    /// <summary>What <see cref="Populate"/> lays down that travels, sorted.</summary>
    public static readonly string[] Travelling =
    {
        @"ModConfigs\DvrmentConfs\Preset-kieracustom.txt",
        @"ModConfigs\DvrmentConfs\current.json",
        @"ModConfigs\SBCameraScroll.txt",
        @"ModConfigs\devourment.txt",
        @"ModConfigs\moreslugcats.txt",
    };

    /// <summary>What <see cref="Populate"/> lays down that does not travel.</summary>
    public static readonly string[] StaysBehind =
    {
        @"ModConfigs\MapOptions\cache.json",
        @"ModConfigs\steam_autocloud.vdf",
        @"ModConfigs\willowwisp.bellyplus.json",
    };
}
