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
    int NowOn)
{
    public static ModSyncResult Refused(string problem) => new(false, problem, 0, 0, 0);

    public string Headline => Applied
        ? string.Format(
            CultureInfo.CurrentCulture,
            "{0} on, {1} turned on, {2} turned off. Start Rain World to play with them.",
            NowOn,
            TurnedOn,
            TurnedOff)
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
        BackupService? backups = null)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            throw new ArgumentException("The save folder is required.", nameof(saveRoot));
        }

        SaveRoot = Path.GetFullPath(saveRoot.Trim());
        GameInstallPath = string.IsNullOrWhiteSpace(gameInstallPath) ? null : Path.GetFullPath(gameInstallPath.Trim());
        _gameDetector = gameDetector ?? throw new ArgumentNullException(nameof(gameDetector));
        Store = store ?? new ModStateStore();
        _backups = backups;
    }

    public string SaveRoot { get; }

    public string? GameInstallPath { get; }

    public ModStateStore Store { get; }

    public CurrentMods ReadCurrent() => CurrentModsReader.Read(SaveRoot, GameInstallPath);

    public ModSyncPlan BuildPlan(ModListSnapshot? recorded) => ModSyncPlan.Build(recorded, ReadCurrent());

    public ModListSnapshot ImportList(string path) => ModListFile.Read(path);

    public int ExportList(ModSyncPlan plan, string path)
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

        var snapshot = new ModListSnapshot
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

        ModListFile.Write(path, snapshot);
        return snapshot.Mods.Count;
    }

    public ModStateRestorePoint? ReadRestorePoint() => Store.Read();

    public string? WhyNotNow()
    {
        if (_gameDetector.IsGameRunning(out string? processName))
        {
            return $"Rain World is running (process \"{processName}\"). Close the game before changing which mods are on.";
        }

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

        if (WhyNotNow() is { } refusal)
        {
            return ModSyncResult.Refused(refusal);
        }

        CurrentMods current = ReadCurrent();
        if (!current.Enabled.ReadTheEnabledList)
        {
            return ModSyncResult.Refused(
                "Which mods are on could not be read, so changing them would be a guess. " + current.Enabled.Note);
        }

        ModSyncOutcome outcome = plan.Resolve(current);
        return Write(current, outcome, because ?? "matching a save's mods");
    }

    public ModSyncResult RestorePrevious()
    {
        ModStateRestorePoint? point = Store.Read();

        if (point is null)
        {
            return ModSyncResult.Refused("There is no earlier mod list to go back to.");
        }

        if (!point.UsableForRestore)
        {
            return ModSyncResult.Refused("The earlier mod list was not recorded fully enough to put back.");
        }

        if (WhyNotNow() is { } refusal)
        {
            return ModSyncResult.Refused(refusal);
        }

        CurrentMods current = ReadCurrent();
        if (!current.Enabled.ReadTheEnabledList)
        {
            return ModSyncResult.Refused(
                "Which mods are on could not be read, so changing them would be a guess. " + current.Enabled.Note);
        }

        // Restoring is the same operation as matching a save, with the saved list standing in for the
        // recording, so there is one write path rather than two that can drift apart.
        ModSyncPlan plan = ModSyncPlan.Build(point.Mods, current);
        return Write(current, plan.Resolve(current), "going back to the earlier mod list");
    }

    private ModSyncResult Write(CurrentMods current, ModSyncOutcome outcome, string because)
    {
        string optionsPath = Path.Combine(SaveRoot, OptionsFile.FileName);
        string listPath = EnabledModsFile.PathTo(GameInstallPath)!;

        byte[] originalOptions;
        byte[] newOptions;
        try
        {
            originalOptions = File.ReadAllBytes(optionsPath);
            newOptions = OptionsWriter.Rewrite(originalOptions, outcome.EnabledIds, outcome.LoadOrder);
        }
        catch (Exception ex) when (ex is SaveContainerException or IOException or UnauthorizedAccessException)
        {
            return ModSyncResult.Refused($"The options file could not be prepared ({ex.Message}), so nothing was changed.");
        }

        IReadOnlyList<string> existingLines = EnabledModsFile.Read(GameInstallPath) ?? Array.Empty<string>();
        IReadOnlyList<string> newLines = EnabledModsFile.Rewrite(
            existingLines,
            outcome.TurnOn,
            outcome.TurnOff,
            CurrentModsReader.WorkshopContentPath(GameInstallPath));

        string optionsStaged = optionsPath + ".rwc-tmp";
        string listStaged = listPath + ".rwc-tmp";

        try
        {
            if (Stage(optionsStaged, listStaged, newOptions, newLines, outcome) is { } staging)
            {
                return ModSyncResult.Refused(staging);
            }

            // Written before either file moves, so a crash between the two moves still leaves a way
            // back to the list that was on.
            try
            {
                Store.Write(new ModStateRestorePoint
                {
                    TakenAt = DateTimeOffset.Now,
                    Mods = current.Enabled,
                    EnabledModsLines = existingLines.ToList(),
                    Because = because,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ModSyncResult.Refused(
                    $"The list you have now could not be saved to go back to ({ex.Message}), so nothing was changed.");
            }

            using IDisposable? lease = _backups?.AcquireOperationLock();

            // Re-checked inside the lock: staging and hashing take long enough for somebody to start
            // the game in the middle of it.
            if (_gameDetector.IsGameRunning(out string? processName))
            {
                throw new GameRunningException(processName ?? "RainWorld");
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
        catch (GameRunningException)
        {
            throw;
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

        return new ModSyncResult(
            true,
            null,
            outcome.TurnOn.Count,
            outcome.TurnOff.Count,
            outcome.EnabledIds.Count);
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
                + "has not. Use Restore my previous mods, or start the game and set them in Remix.");
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
