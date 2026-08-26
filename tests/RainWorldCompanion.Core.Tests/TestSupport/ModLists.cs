using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>Hand-built lists. Reading a real machine is covered by CurrentModsReaderTests.</summary>
internal static class ModLists
{
    /// <summary>Version and workshop id are optional: a mod can ship without a version, and a local mod has no workshop page.</summary>
    public static ModEntry Mod(string id, string? version = null, string? workshopId = null, string? name = null)
        => new()
        {
            Id = id,
            Name = name ?? id,
            Version = version,
            WorkshopId = workshopId,
            Origin = workshopId is null ? ModEntry.InstallOrigin : ModEntry.WorkshopOrigin,
        };

    /// <summary>A list that was read in full, which is the ordinary case.</summary>
    public static ModListSnapshot Snapshot(string? gameVersion, params ModEntry[] mods)
        => new()
        {
            ReadTheEnabledList = true,
            CheckedTheInstall = true,
            CheckedTheWorkshop = true,
            GameVersion = gameVersion,
            Mods = mods.ToList(),
        };

    /// <summary>What a machine looks like when every mod named is on and installed.</summary>
    public static CurrentMods Current(string? gameVersion, params ModEntry[] mods)
    {
        ModListSnapshot enabled = Snapshot(gameVersion, mods);
        return new CurrentMods(enabled, enabled.Mods.ToList());
    }

    /// <summary>A read that found nothing, which must never be read as "no mods were on".</summary>
    public static CurrentMods CouldNotLook()
        => CurrentMods.NothingRead("The save folder holds no options file.");
}
