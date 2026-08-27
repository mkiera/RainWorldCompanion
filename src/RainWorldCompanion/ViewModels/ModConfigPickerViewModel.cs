using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// One mod's settings, ticked or not. A row is a mod rather than a file, because Devourment owns
/// both its settings file and its whole preset folder and ticking Devourment means both.
/// </summary>
public sealed partial class ModConfigRowViewModel : ObservableObject
{
    /// <summary>
    /// Starts off, and the picker never turns one on by itself. Somebody else's settings are not
    /// what a player asked for by asking to load a save.
    /// </summary>
    [ObservableProperty]
    private bool take;

    public required string ModId { get; init; }

    /// <summary>The mod's name and version where the save recorded them, the bare id where it did
    /// not.</summary>
    public required string Name { get; init; }

    /// <summary>How much is being brought across, as files and size.</summary>
    public required string DetailText { get; init; }

    /// <summary>What is worth knowing before ticking it. Empty is the ordinary case.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public bool HasNotes => Notes.Count > 0;
}

/// <summary>
/// The mod settings a save carries, offered one mod at a time. Nothing here can stop a load: the
/// ticks decide what is written and every one of them starts clear.
/// </summary>
public sealed partial class ModConfigPickerViewModel : ObservableObject
{
    private static readonly ModConfigRowViewModel[] NoRows = Array.Empty<ModConfigRowViewModel>();

    public ModConfigPickerViewModel(ModConfigOffer? offer)
    {
        Rows = NoRows;
        HeadlineText = "";
        Fill(offer);
    }

    public IReadOnlyList<ModConfigRowViewModel> Rows { get; private set; }

    public string HeadlineText { get; private set; }

    public bool HasRows => Rows.Count > 0;

    /// <summary>One row is its own select all, so the control would be noise beside it.</summary>
    public bool HasSeveralRows => Rows.Count > 1;

    public string TakeAllText => "Take every mod's settings";

    /// <summary>
    /// Null when some but not all are ticked, which is the indeterminate a three state box draws.
    /// Reading it walks the rows rather than keeping a second copy of what they already say.
    /// </summary>
    public bool? TakeAll
    {
        get
        {
            var taken = 0;

            foreach (var row in Rows)
            {
                if (row.Take)
                {
                    taken++;
                }
            }

            return taken == 0 ? false : taken == Rows.Count ? true : null;
        }

        set
        {
            // Indeterminate is a state the box reports, never one to apply: there is no sensible
            // set of rows to leave behind for it.
            if (value is not { } wanted || _applying)
            {
                return;
            }

            // Held shut across the sweep and the one announcement that follows it. Announcing row
            // by row would report indeterminate part way through, and a two state box answers that
            // by writing back the opposite, which lands straight back here. Covering the
            // announcement too means such a write back is ignored rather than recursing.
            _applying = true;

            try
            {
                foreach (var row in Rows)
                {
                    row.Take = wanted;
                }

                OnPropertyChanged(nameof(TakeAll));
            }
            finally
            {
                _applying = false;
            }
        }
    }

    /// <summary>Hidden when the save carries nothing to pick, which is most saves.</summary>
    public bool ShowSection => HasRows;

    /// <summary>
    /// Stands under the rows whether or not anything is ticked, because it is true of mod settings
    /// in general rather than of any one row.
    /// </summary>
    public string FooterText =>
        "Mod settings can hold things that belong to one machine, such as a window size. " +
        "Whatever you take is put back by restoring the safety copy this load makes.";

    /// <summary>The mods whose settings were ticked, which is what a load is given.</summary>
    public IReadOnlyCollection<string> Chosen
    {
        get
        {
            var chosen = new List<string>();

            foreach (var row in Rows)
            {
                if (row.Take)
                {
                    chosen.Add(row.ModId);
                }
            }

            return chosen;
        }
    }

    /// <summary>
    /// Rebuilds the wording against a fresh offer, keeping every tick the user has already made.
    /// The Mods window can be opened from the same dialog, and a mod turned on there changes what
    /// a row says about it without changing what was asked for.
    /// </summary>
    public void Reload(ModConfigOffer? offer)
    {
        var ticked = new HashSet<string>(Chosen, StringComparer.OrdinalIgnoreCase);

        Fill(offer);

        foreach (var row in Rows)
        {
            row.Take = ticked.Contains(row.ModId);
        }
    }

    /// <summary>
    /// Rows are watched rather than asked, so the select all box follows a tick made on any one of
    /// them. The old rows are dropped first, or a reloaded picker would keep answering for them.
    /// </summary>
    private void Fill(ModConfigOffer? offer)
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows = offer is null ? NoRows : BuildRows(offer);
        HeadlineText = offer is null ? "" : Headline(offer, Rows.Count);

        foreach (var row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    private bool _applying;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applying && e.PropertyName is nameof(ModConfigRowViewModel.Take))
        {
            OnPropertyChanged(nameof(TakeAll));
        }
    }

    private static string Headline(ModConfigOffer offer, int count)
    {
        var opening = count == 1
            ? "This save carries the settings of 1 mod."
            : $"This save carries the settings of {Number(count)} mods.";

        // Said once here rather than on every row, where it would be the same sentence repeated.
        return offer.RecordedMods is null
            ? opening + " No mod list was recorded with it, so each one is named by its settings file."
            : opening + " Tick the ones whose settings you want.";
    }

    private static IReadOnlyList<ModConfigRowViewModel> BuildRows(ModConfigOffer offer)
    {
        var rows = new List<ModConfigRowViewModel>();

        foreach (var group in offer.ByMod())
        {
            var mod = Find(offer.RecordedMods, group.ModId);

            rows.Add(new ModConfigRowViewModel
            {
                ModId = group.ModId,
                Name = NameFor(group.ModId, mod),
                DetailText = Detail(group),
                Notes = Notes(offer, group, mod),
            });
        }

        return rows;
    }

    private static ModEntry? Find(ModListSnapshot? mods, string modId)
    {
        if (mods is null)
        {
            return null;
        }

        foreach (var mod in mods.Mods)
        {
            if (string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase))
            {
                return mod;
            }
        }

        return null;
    }

    private static string NameFor(string modId, ModEntry? mod)
    {
        if (mod is null)
        {
            return modId.Length == 0 ? "Settings with no mod name" : modId;
        }

        var name = string.IsNullOrWhiteSpace(mod.Name) ? modId : mod.Name.Trim();
        return string.IsNullOrWhiteSpace(mod.Version) ? name : name + "  " + mod.Version.Trim();
    }

    private static string Detail(ModConfigGroup group)
    {
        var files = group.Files.Count == 1 ? "1 file" : Number(group.Files.Count) + " files";
        return files + "    " + SlotCopyService.FormatSize(group.TotalBytes);
    }

    private static IReadOnlyList<string> Notes(ModConfigOffer offer, ModConfigGroup group, ModEntry? mod)
    {
        var notes = new List<string>();

        if (Replaces(offer.Live, group.ModId))
        {
            notes.Add("Replaces the settings you have for this mod.");
        }

        // A settings file sits in ModConfigs whether its mod is on or not, so a save can carry
        // settings for a mod its own list never named.
        if (mod is null && offer.RecordedMods is not null)
        {
            notes.Add("This mod was not in the list recorded with the save.");
        }

        if (NotInstalledHere(offer, group.ModId))
        {
            notes.Add("This mod is not installed here, so nothing will read these until it is.");
        }

        foreach (var file in group.Files)
        {
            if (offer.MachineSpecific.TryGetValue(file.RelativePath, out var keys) && keys.Count > 0)
            {
                notes.Add($"{file.RelativePath} also sets {string.Join(", ", keys)}, which belong to one machine.");
            }
        }

        return notes;
    }

    private static bool Replaces(ModConfigSet? live, string modId)
    {
        if (live is not { ReadTheFolder: true })
        {
            return false;
        }

        foreach (var file in live.Files)
        {
            if (string.Equals(file.ModId, modId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Said only when the install was actually looked at, following the rule
    /// <see cref="ModListSectionViewModel"/> follows: a folder nobody could read is not a mod
    /// nobody has.
    /// </summary>
    private static bool NotInstalledHere(ModConfigOffer offer, string modId)
    {
        if (offer.Current is not { } current || !current.Enabled.CheckedTheInstall)
        {
            return false;
        }

        foreach (var mod in current.Installed)
        {
            if (string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
