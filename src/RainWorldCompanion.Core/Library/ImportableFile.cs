using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Library;

public enum ImportableKind
{
    Unknown,
    Save,
    ModSettings,
    ModList,
}

public static class ImportableFile
{
    // By name only, so a drop is sorted without opening anything. The importer each kind lands in
    // still checks the bytes.
    public static ImportableKind Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ImportableKind.Unknown;
        }

        string name = Path.GetFileName(path.Trim());
        string extension = Path.GetExtension(name);

        if (Is(extension, SaveBundle.Extension) || Is(extension, SaveBundle.CampaignExtension))
        {
            return ImportableKind.Save;
        }

        if (Is(extension, ModConfigArchive.Extension))
        {
            return ImportableKind.ModSettings;
        }

        if (Is(extension, ModListFile.Extension))
        {
            return ImportableKind.ModList;
        }

        return SaveSlotRef.ForFileName(name) is not null ? ImportableKind.Save : ImportableKind.Unknown;
    }

    private static bool Is(string extension, string wanted)
        => string.Equals(extension, wanted, StringComparison.OrdinalIgnoreCase);
}
