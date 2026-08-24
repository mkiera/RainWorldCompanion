// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// What a slot pair needs from the window in order to offer the copy: whether a copy may start at
/// all right now, and where to send the request.
/// </summary>
/// <param name="CanCopy">
/// Read every time a button re-evaluates, so the same gate that disables New Backup and Restore
/// while the game is running or an operation is in flight disables these too.
/// </param>
/// <param name="Request">
/// Called with the slot number and the direction. True means the local file is copied onto the
/// online one, false means the other way.
/// </param>
public sealed record SlotCopyGate(Func<bool> CanCopy, Action<int, bool> Request);

/// <summary>
/// One slot number with its local save and its Rain Meadow online save beside each other, and the
/// copy between them.
///
/// Rain Meadow hooks Options.GetSaveFileName_SavOrExp and swaps sav for online_sav while a lobby
/// is joined, so the game's own slot number picks both files. Slot 2 is sav2 and online_sav2, and
/// pairing them is what lets a player see the two halves of one menu slot together.
/// </summary>
public sealed class SlotPairViewModel
{
    private readonly SlotCopyGate? _gate;

    public SlotPairViewModel(int slotNumber, SlotViewModel? local, SlotViewModel? online, SlotCopyGate? gate)
    {
        SlotNumber = slotNumber;
        _gate = gate;

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

        ShowCopy = gate is not null;

        CopyToOnlineToolTip = "Replace " + onlineName + " with a byte for byte copy of " + localName + ".";
        CopyToLocalToolTip = "Replace " + localName + " with a byte for byte copy of " + onlineName + ".";
        CopyToOnlineAccessibleName = "Copy " + localName + " onto " + onlineName;
        CopyToLocalAccessibleName = "Copy " + onlineName + " onto " + localName;

        CopyToOnlineCommand = new RelayCommand(
            () => _gate?.Request(SlotNumber, true),
            () => CanCopyToOnline);

        CopyToLocalCommand = new RelayCommand(
            () => _gate?.Request(SlotNumber, false),
            () => CanCopyToLocal);
    }

    /// <summary>1, 2 or 3, the number the game's own menu shows.</summary>
    public int SlotNumber { get; }

    public string NumberText { get; }

    public string HeaderText { get; }

    public SlotSideViewModel Local { get; }

    public SlotSideViewModel Online { get; }

    /// <summary>
    /// False for a backup, where there is nothing to copy: the files in a snapshot are put back by
    /// restoring it, not by writing one of them over a live slot.
    /// </summary>
    public bool ShowCopy { get; }

    public RelayCommand CopyToOnlineCommand { get; }

    public RelayCommand CopyToLocalCommand { get; }

    public string CopyToOnlineToolTip { get; }

    public string CopyToLocalToolTip { get; }

    public string CopyToOnlineAccessibleName { get; }

    public string CopyToLocalAccessibleName { get; }

    /// <summary>What a screen reader announces for the row as a whole.</summary>
    public string AccessibleName =>
        "Slot " + NumberText + ". Local, " + Local.SummaryText + ". Online, " + Online.SummaryText + ".";

    private bool CanCopyToOnline => Local.Exists && (_gate?.CanCopy() ?? false);

    private bool CanCopyToLocal => Online.Exists && (_gate?.CanCopy() ?? false);

    /// <summary>
    /// Re-asks both buttons whether they are allowed to run. Called by the window when the game
    /// starts or stops, or when an operation begins or ends.
    /// </summary>
    public void RaiseCopyStates()
    {
        CopyToOnlineCommand.NotifyCanExecuteChanged();
        CopyToLocalCommand.NotifyCanExecuteChanged();
    }
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
