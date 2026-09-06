// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Mods;

public sealed record ModSyncResult(
    bool Applied,
    string? Problem,
    int TurnedOn,
    int TurnedOff,
    int NowOn,
    string? Warning = null)
{
    public static ModSyncResult Refused(string problem) => new(false, problem, 0, 0, 0);

    public string Headline => Applied
        ? string.Format(
            CultureInfo.CurrentCulture,
            "{0} on, {1} turned on, {2} turned off. Start Rain World to play with them.",
            NowOn,
            TurnedOn,
            TurnedOff) + (Warning is { Length: > 0 } warning ? " " + warning : "")
        : Problem ?? "Nothing was changed.";
}

public sealed class ModSyncService
{
    private readonly IGameProcessDetector _gameDetector;
    private readonly BackupService? _backups;

    public ModSyncService(
        string saveRoot,
        string? gameInstallPath,
        IGameProcessDetector gameDetector,
        ModStateStore? store = null,
        BackupService? backups = null,
        ModListCatalog? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            throw new ArgumentException("The save folder is required.", nameof(saveRoot));
        }

        SaveRoot = Path.GetFullPath(saveRoot.Trim());
        GameInstallPath = string.IsNullOrWhiteSpace(gameInstallPath) ? null : Path.GetFullPath(gameInstallPath.Trim());
        _gameDetector = gameDetector ?? throw new ArgumentNullException(nameof(gameDetector));
        var legacyStore = store ?? new ModStateStore();
        Catalog = catalog ?? new ModListCatalog(legacyStore.Folder);
        _backups = backups;
    }

    public string SaveRoot { get; }

    public string? GameInstallPath { get; }

    public ModListCatalog Catalog { get; }

    public CurrentMods ReadCurrent() => CurrentModsReader.Read(SaveRoot, GameInstallPath);

    public ModSyncPlan BuildPlan(ModListSnapshot? recorded) => ModSyncPlan.Build(recorded, ReadCurrent());

    public ModListSnapshot ImportList(string path) => ModListFile.Read(path);

    public int ExportList(ModSyncPlan plan, string path)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ModListSnapshot snapshot = Snapshot(plan);
        ModListFile.Write(path, snapshot);
        return snapshot.Mods.Count;
    }

    public ModListSnapshot Snapshot(ModSyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CurrentMods current = ReadCurrent();
        var currentOrder = current.Enabled.Mods
            .Select((mod, index) => (mod.Id, Order: mod.LoadOrder ?? index))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Order, StringComparer.OrdinalIgnoreCase);

        List<ModSyncRow> selected = plan.Rows
            .Where(row => row.Wanted)
            .OrderBy(row => row.WantedLoadOrder ?? currentOrder.GetValueOrDefault(row.Id, int.MaxValue))
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ModListSnapshot
        {
            GameVersion = plan.Diff.RecordedGameVersion ?? current.Enabled.GameVersion,
            ReadTheEnabledList = true,
            CheckedTheInstall = true,
            CheckedTheWorkshop = true,
            Mods = selected.Select((row, index) => new ModEntry
            {
                Id = row.Id,
                Name = row.Name,
                Version = row.RecordedVersion ?? row.Version,
                WorkshopId = row.WorkshopId,
                LoadOrder = index,
            }).ToList(),
        };
    }

    public ModListCatalogView ReadCatalog() => Catalog.Read();

    public string? WhyNotNow()
    {
        if (_gameDetector.IsGameRunning(out string? processName))
        {
            return $"Rain World is running (process \"{processName}\"). Close the game before changing which mods are on.";
        }

        return WhyFilesCannotChange();
    }

    private string? WhyFilesCannotChange()
    {

        if (GameInstallPath is null)
        {
            return "The game folder is not set, so the mod loader's list cannot be written. Set it in Settings.";
        }

        if (!GameInstallLocator.LooksLikeInstall(GameInstallPath))
        {
            return $"'{GameInstallPath}' does not look like a Rain World install, so the mod loader's list cannot be written.";
        }

        if (EnabledModsFile.PathTo(GameInstallPath) is not { } listPath || !File.Exists(listPath))
        {
            return "The game folder holds no enabledMods.txt, so which mods load cannot be changed. Start Rain World once first.";
        }

        if (!File.Exists(Path.Combine(SaveRoot, OptionsFile.FileName)))
        {
            return "The save folder holds no options file, so which mods are on cannot be changed. Start Rain World once first.";
        }

        return null;
    }

    public ModSyncResult Apply(ModSyncPlan plan, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        CurrentMods current = ReadCurrent();
        if (!current.Enabled.ReadTheEnabledList)
        {
            return ModSyncResult.Refused(
                "Which mods are on could not be read, so changing them would be a guess. " + current.Enabled.Note);
        }

        if (!PlanMatchesCurrent(plan, current.Enabled))
        {
            return ModSyncResult.Refused("The live mod list changed after this preview was built. Refresh and try again.");
        }

        ModSyncOutcome outcome = plan.Resolve(current);
        if (!Changes(current, outcome))
        {
            return new ModSyncResult(false, null, 0, 0, current.Enabled.Mods.Count);
        }

        if (WhyFilesCannotChange() is { } refusal)
        {
            return ModSyncResult.Refused(refusal);
        }

        return Write(current, outcome, because ?? "Before manual mod changes");
    }

    private ModSyncResult Write(CurrentMods current, ModSyncOutcome outcome, string because)
    {
        string optionsPath = Path.Combine(SaveRoot, OptionsFile.FileName);
        string listPath = EnabledModsFile.PathTo(GameInstallPath)!;

        byte[] originalOptions;
        byte[] originalList;
        byte[] newOptions;
        try
        {
            originalOptions = File.ReadAllBytes(optionsPath);
            originalList = File.ReadAllBytes(listPath);
            newOptions = OptionsWriter.Rewrite(originalOptions, outcome.EnabledIds, outcome.LoadOrder);
        }
        catch (Exception ex) when (ex is SaveContainerException or IOException or UnauthorizedAccessException)
        {
            return ModSyncResult.Refused($"The live mod files could not be prepared ({ex.Message}), so nothing was changed.");
        }

        IReadOnlyList<string>? existingLines = EnabledModsFile.Read(GameInstallPath);
        if (existingLines is null)
        {
            return ModSyncResult.Refused("The current enabledMods.txt could not be read, so nothing was changed.");
        }

        IReadOnlyList<string> newLines = EnabledModsFile.Rewrite(
            existingLines,
            outcome.TurnOn,
            outcome.TurnOff,
            CurrentModsReader.WorkshopContentPath(GameInstallPath));

        string stagingId = Guid.NewGuid().ToString("N");
        string optionsStaged = optionsPath + "." + stagingId + ".rwc-tmp";
        string listStaged = listPath + "." + stagingId + ".rwc-tmp";
        string? captureWarning = null;

        try
        {
            if (Stage(optionsStaged, listStaged, newOptions, newLines, outcome) is { } staging)
            {
                return ModSyncResult.Refused(staging);
            }

            using IDisposable? lease = _backups?.AcquireOperationLock();

            if (_gameDetector.IsGameRunning(out string? processName))
            {
                return ModSyncResult.Refused(
                    $"Rain World is running (process \"{processName ?? "RainWorld"}\"). Close the game before changing which mods are on.");
            }

            CurrentMods lockedCurrent = ReadCurrent();
            if (!SameState(current.Enabled, lockedCurrent.Enabled)
                || !File.ReadAllBytes(optionsPath).SequenceEqual(originalOptions)
                || !File.ReadAllBytes(listPath).SequenceEqual(originalList))
            {
                return ModSyncResult.Refused("The mod list changed while Apply was being prepared. Refresh and try again.");
            }

            ModListCatalogResult captured = Catalog.Execute(new AppendHistory(lockedCurrent.Enabled, because));
            if (!captured.Succeeded)
            {
                return ModSyncResult.Refused(
                    $"The list you have now could not be saved for recovery ({captured.Problem}), so nothing was changed.");
            }

            captureWarning = captured.Warning;

            if (_gameDetector.IsGameRunning(out processName))
            {
                return ModSyncResult.Refused(
                    $"Rain World started while Apply was being prepared (process \"{processName ?? "RainWorld"}\"). Nothing was changed.");
            }

            if (!File.ReadAllBytes(optionsPath).SequenceEqual(originalOptions)
                || !File.ReadAllBytes(listPath).SequenceEqual(originalList))
            {
                return ModSyncResult.Refused("The mod list changed while its recovery entry was being saved. Refresh and try again.");
            }

            File.Move(optionsStaged, optionsPath, overwrite: true);

            // The pair cannot move as one, so a failure here puts the first file back rather than
            // leaving the game and its loader describing different mods.
            try
            {
                File.Move(listStaged, listPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PutOptionsBack(optionsPath, originalOptions, ex.Message);
            }
        }
        catch (BackupBusyException ex)
        {
            return ModSyncResult.Refused(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModSyncResult.Refused($"The mod list could not be written ({ex.Message}).");
        }
        finally
        {
            Delete(optionsStaged);
            Delete(listStaged);
        }

        ModListCatalogResult pruned = Catalog.Execute(new PruneModListHistory());
        string? pruneWarning = pruned.Succeeded
            ? pruned.Warning
            : "The mod list changed, but old recovery entries could not be cleaned up. " + pruned.Problem;
        string? warning = JoinWarnings(captureWarning, pruneWarning);

        return new ModSyncResult(
            true,
            null,
            outcome.TurnOn.Count,
            outcome.TurnOff.Count,
            outcome.EnabledIds.Count,
            warning);
    }

    private static bool Changes(CurrentMods current, ModSyncOutcome outcome)
    {
        var currentIds = new HashSet<string>(current.Enabled.Mods.Select(mod => mod.Id), StringComparer.OrdinalIgnoreCase);
        if (!currentIds.SetEquals(outcome.EnabledIds))
        {
            return true;
        }

        var currentOrder = current.Enabled.Mods
            .Where(mod => mod.LoadOrder is not null)
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().LoadOrder!.Value, StringComparer.OrdinalIgnoreCase);

        return outcome.LoadOrder.Any(item => currentOrder.GetValueOrDefault(item.Key, int.MinValue) != item.Value);
    }

    private static bool PlanMatchesCurrent(ModSyncPlan plan, ModListSnapshot current)
    {
        var plannedIds = new HashSet<string>(
            plan.Rows.Where(row => row.IsOn).Select(row => row.Id),
            StringComparer.OrdinalIgnoreCase);
        var currentIds = new HashSet<string>(current.Mods.Select(mod => mod.Id), StringComparer.OrdinalIgnoreCase);
        if (!plannedIds.SetEquals(currentIds))
        {
            return false;
        }

        var plannedOrder = plan.Rows
            .Where(row => row.IsOn)
            .GroupBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().CurrentLoadOrder, StringComparer.OrdinalIgnoreCase);
        var currentOrder = current.Mods
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().LoadOrder, StringComparer.OrdinalIgnoreCase);
        return plannedOrder.All(item => currentOrder.TryGetValue(item.Key, out int? order) && item.Value == order);
    }

    private static bool SameState(ModListSnapshot first, ModListSnapshot second)
    {
        var firstIds = new HashSet<string>(first.Mods.Select(mod => mod.Id), StringComparer.OrdinalIgnoreCase);
        var secondIds = new HashSet<string>(second.Mods.Select(mod => mod.Id), StringComparer.OrdinalIgnoreCase);
        if (!firstIds.SetEquals(secondIds))
        {
            return false;
        }

        var firstOrder = first.Mods
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().LoadOrder, StringComparer.OrdinalIgnoreCase);
        var secondOrder = second.Mods
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().LoadOrder, StringComparer.OrdinalIgnoreCase);
        return firstOrder.All(item => secondOrder.TryGetValue(item.Key, out int? order) && item.Value == order);
    }

    private static string? JoinWarnings(params string?[] warnings)
    {
        string[] present = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Select(warning => warning!).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }

    private string? Stage(
        string optionsStaged,
        string listStaged,
        byte[] newOptions,
        IReadOnlyList<string> newLines,
        ModSyncOutcome outcome)
    {
        try
        {
            File.WriteAllBytes(optionsStaged, newOptions);
            File.WriteAllLines(listStaged, newLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The new mod list could not be prepared ({ex.Message}), so nothing was changed.";
        }

        OptionsRead read;
        try
        {
            read = OptionsFile.ReadFile(optionsStaged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The new options file could not be read back ({ex.Message}), so nothing was changed.";
        }

        if (!read.Read)
        {
            return $"The new options file did not read back correctly ({read.Problem}), so nothing was changed.";
        }

        // Set rather than sequence: the writer keeps the file's own order, which is not the order
        // the plan resolved them in.
        if (!new HashSet<string>(read.EnabledModIds, StringComparer.OrdinalIgnoreCase)
                .SetEquals(outcome.EnabledIds))
        {
            return "The new options file came back holding a different mod list than it was given, so nothing was changed.";
        }

        foreach ((string id, int position) in outcome.LoadOrder)
        {
            if (!read.LoadOrder.TryGetValue(id, out int written) || written != position)
            {
                return "The new options file came back with a different load order than it was given, so nothing was changed.";
            }
        }

        string[] linesBack;
        try
        {
            linesBack = File.ReadAllLines(listStaged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The new loader list could not be read back ({ex.Message}), so nothing was changed.";
        }

        return linesBack.SequenceEqual(newLines, StringComparer.Ordinal)
            ? null
            : "The new loader list came back different from what it was given, so nothing was changed.";
    }

    private static ModSyncResult PutOptionsBack(string optionsPath, byte[] original, string why)
    {
        try
        {
            File.WriteAllBytes(optionsPath, original);
            return ModSyncResult.Refused(
                $"The mod loader's list could not be written ({why}), so nothing was changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModSyncResult.Refused(
                $"The mod loader's list could not be written ({why}), and putting the options file "
                + $"back failed too ({ex.Message}). Which mods are on has changed but which ones load "
                + "has not. Open Saved lists to preview the captured list, or set the mods in Remix.");
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
