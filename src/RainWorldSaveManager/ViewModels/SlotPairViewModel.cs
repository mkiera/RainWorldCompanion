// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// One slot number with its local save and its Rain Meadow online save beside each other.
///
/// Rain Meadow hooks Options.GetSaveFileName_SavOrExp and swaps sav for online_sav while a lobby
/// is joined, so the game's own slot number picks both files. Slot 2 is sav2 and online_sav2, and
/// pairing them is what lets a player see the two halves of one menu slot together.
///
/// The row shows what is in each file. Copying between slots is one command in the window's top
/// bar, so the row carries no buttons of its own and reads the same in a backup as it does live.
/// </summary>
public sealed class SlotPairViewModel
{
    public SlotPairViewModel(int slotNumber, SlotViewModel? local, SlotViewModel? online)
    {
        SlotNumber = slotNumber;

        NumberText = slotNumber.ToString(CultureInfo.InvariantCulture);
        HeaderText = "SLOT " + NumberText;

        // A row has to be able to name a file that is not there, and a file that is not there has
        // no SlotMetadata to take a name from. SaveSlotRef is the one place the naming rule lives,
        // so the fallback asks it rather than spelling the rule out again.
        string localName = local?.FileName is { Length: > 0 } name
            ? name
            : new SaveSlotRef(SaveRealm.Local, slotNumber).FileName;
        string onlineName = online?.FileName is { Length: > 0 } onlineFile
            ? onlineFile
            : new SaveSlotRef(SaveRealm.Online, slotNumber).FileName;

        Local = new SlotSideViewModel("LOCAL", localName, local);
        Online = new SlotSideViewModel("ONLINE", onlineName, online);
    }

    /// <summary>1, 2 or 3, the number the game's own menu shows.</summary>
    public int SlotNumber { get; }

    public string NumberText { get; }

    public string HeaderText { get; }

    public SlotSideViewModel Local { get; }

    public SlotSideViewModel Online { get; }

    /// <summary>What a screen reader announces for the row as a whole.</summary>
    public string AccessibleName =>
        "Slot " + NumberText + ". Local, " + Local.SummaryText + ". Online, " + Online.SummaryText + ".";
}

/// <summary>
/// One half of a slot pair, drawn the same way whichever half it is. A side with no file behind it
/// still renders, because "there is no online save in this slot yet" is the state a player copying
/// a local save across is starting from.
/// </summary>
public sealed class SlotSideViewModel
{
    public SlotSideViewModel(string kindLabel, string fileName, SlotViewModel? slot)
    {
        KindLabel = kindLabel;
        FileName = fileName;

        Exists = slot is not null;
        SummaryText = slot?.SummaryText ?? "no file";
        Portraits = slot?.Portraits ?? Array.Empty<PortraitViewModel>();
        ChecksumBad = slot?.ChecksumBad ?? false;
    }

    /// <summary>"LOCAL" or "ONLINE".</summary>
    public string KindLabel { get; }

    /// <summary>sav2 or online_sav2, whether or not the file is there.</summary>
    public string FileName { get; }

    public bool Exists { get; }

    public string SummaryText { get; }

    public IReadOnlyList<PortraitViewModel> Portraits { get; }

    public bool HasPortraits => Portraits.Count > 0;

    /// <summary>True only when the file carried a digest and it did not recompute.</summary>
    public bool ChecksumBad { get; }
}
