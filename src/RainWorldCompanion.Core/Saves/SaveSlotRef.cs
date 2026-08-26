// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// One slot number picks two files that sit side by side in the save folder, and this says which of
/// them is meant. A lobby joined from an Expedition slot writes names such as online_sav-1 and
/// online_sav0, which are not menu slots: <see cref="SaveSlotRef.ForFileName"/> returns null for
/// them, but the backup scope covers them so the save is not invisible to the app.
/// </summary>
public enum SaveRealm
{
    /// <summary>sav, sav2, sav3. Also what a manifest written before online slots existed means.</summary>
    Local,

    /// <summary>online_sav, online_sav2, online_sav3, written while playing in a Rain Meadow lobby.</summary>
    Online,
}

/// <summary>Slot numbers here are the ones the game's menu shows, 1 to 3, so slot 2 means sav2
/// locally and online_sav2 online. The only place the rule turning a slot into a file name lives.</summary>
public sealed record SaveSlotRef(SaveRealm Realm, int Slot)
{
    /// <summary>The lowest and highest slot the game's menu offers.</summary>
    public const int MinSlot = 1;

    public const int MaxSlot = 3;

    /// <summary>What Rain Meadow puts in front of the container name while a lobby is joined.</summary>
    private const string OnlinePrefix = "online_";

    private const string LocalStem = "sav";

    public bool IsRealSlot => Slot >= MinSlot && Slot <= MaxSlot;

    /// <summary>Empty for a slot number the game does not have. Slot 1 carries no number, so "sav"
    /// and "online_sav" are the slot 1 names and the suffix only appears from slot 2 on.</summary>
    public string FileName => FileNameFor(Realm, Slot) ?? "";

    /// <summary>Null rather than an exception for a slot number outside the game's range.</summary>
    public static string? FileNameFor(SaveRealm realm, int slot)
    {
        if (slot < MinSlot || slot > MaxSlot)
        {
            return null;
        }

        string suffix = slot == MinSlot ? "" : slot.ToString(CultureInfo.InvariantCulture);
        return realm == SaveRealm.Online
            ? OnlinePrefix + LocalStem + suffix
            : LocalStem + suffix;
    }

    /// <summary>Null when the name is not one of the six. Exact match only: the live save folder
    /// holds "sav - Copy" next to "sav", and a prefix match would pick that up as slot 1.</summary>
    public static SaveSlotRef? ForFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        for (int slot = MinSlot; slot <= MaxSlot; slot++)
        {
            if (string.Equals(FileNameFor(SaveRealm.Local, slot), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return new SaveSlotRef(SaveRealm.Local, slot);
            }

            if (string.Equals(FileNameFor(SaveRealm.Online, slot), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return new SaveSlotRef(SaveRealm.Online, slot);
            }
        }

        return null;
    }

    /// <summary>Looser than <see cref="ForFileName"/> on purpose: the prefix alone settles the realm,
    /// which keeps a copy or a fixture named "online_sav3.bin" on the right side.</summary>
    public static SaveRealm RealmForFileName(string? fileName)
        => !string.IsNullOrEmpty(fileName)
            && fileName.StartsWith(OnlinePrefix, StringComparison.OrdinalIgnoreCase)
            ? SaveRealm.Online
            : SaveRealm.Local;

    public override string ToString()
        => FileName is { Length: > 0 } name
            ? name
            : Realm.ToString() + " slot " + Slot.ToString(CultureInfo.InvariantCulture);
}
