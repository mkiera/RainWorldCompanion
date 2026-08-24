// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// Which of the two sets of container files a slot number names.
///
/// Options.GetSaveFileName_SavOrExp returns "sav" for Options.saveSlot 0 and "sav" + (saveSlot + 1)
/// above it. Rain Meadow hooks that method and returns "online_sav" and "online_sav" + (saveSlot + 1)
/// from the same field once a lobby is joined. One slot number therefore picks two files that sit
/// side by side in the save folder, and this says which of them is meant.
///
/// The hook's guard is saveSlot != 0, not saveSlot > 0: it compiles to
/// <c>ldfld Options::saveSlot; ldc.i4.0; cgt.un</c>, and cgt.un compares unsigned, so a negative
/// slot takes the suffix branch too. Options uses a negative saveSlot for Expedition, so a lobby
/// joined from an Expedition slot writes names such as online_sav-1 and online_sav0. Those are not
/// menu slots and <see cref="SaveSlotRef.ForFileName"/> returns null for them, but the backup scope
/// covers them so the save is not invisible to the app.
/// </summary>
public enum SaveRealm
{
    /// <summary>sav, sav2, sav3. Also what a manifest written before online slots existed means.</summary>
    Local,

    /// <summary>online_sav, online_sav2, online_sav3, written while playing in a Rain Meadow lobby.</summary>
    Online,
}

/// <summary>
/// One save slot: which realm it belongs to and which of the game's three slot numbers it is.
///
/// Slot numbers here are the ones the game's menu shows, 1 to 3, so slot 2 means sav2 locally and
/// online_sav2 online. This is the only place the rule turning a slot into a file name lives, so a
/// caller that needs the name asks for it rather than building it.
/// </summary>
public sealed record SaveSlotRef(SaveRealm Realm, int Slot)
{
    /// <summary>The lowest and highest slot the game's menu offers.</summary>
    public const int MinSlot = 1;

    public const int MaxSlot = 3;

    /// <summary>What Rain Meadow puts in front of the container name while a lobby is joined.</summary>
    private const string OnlinePrefix = "online_";

    private const string LocalStem = "sav";

    /// <summary>Whether the slot number is one the game actually has.</summary>
    public bool IsRealSlot => Slot >= MinSlot && Slot <= MaxSlot;

    /// <summary>
    /// The file the game reads and writes for this slot, or the empty string for a slot number the
    /// game does not have. Slot 1 has no number on it, which is why "sav" and "online_sav" are the
    /// slot 1 names and the suffix only appears from slot 2 on.
    /// </summary>
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

    /// <summary>
    /// The slot a container file name stands for, or null when the name is not one of the six.
    ///
    /// Exact match only. The live save folder holds "sav - Copy" and "sav - Copy (2)" next to
    /// "sav", and a prefix or glob match would pick those up as slot 1.
    /// </summary>
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

    /// <summary>
    /// Which realm a file name belongs to. Looser than <see cref="ForFileName"/> on purpose: the
    /// slot number has to come from an exact name so that "sav - Copy" is not read as slot 1, but
    /// the prefix alone settles the realm, which keeps a copy or a fixture named "online_sav3.bin"
    /// on the right side.
    /// </summary>
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
