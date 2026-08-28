using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Taking mod settings out of a library save with no slot written at all. The save stays where it
/// is, and the safety copy is what makes the settings that do land undoable.
/// </summary>
public class TakeSettingsTests
{
    private static readonly SaveSlotRef LocalOne = new(SaveRealm.Local, 1);
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);

    private const string Devourment = "devourment";
    private const string KarmaControl = "karmacontrol";
    private const string DevourmentPath = @"ModConfigs\devourment.txt";
    private const string KarmaPath = @"ModConfigs\karmacontrol.txt";

    /// <summary>An entry whose settings differ from the ones in the save folder now.</summary>
    private static LibraryEntry StoredWith(LibraryWorld world, string theirs, string yours)
    {
        world.Live.WriteText(DevourmentPath, theirs);
        world.Live.WriteText(KarmaPath, theirs);
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);
        world.Live.WriteText(DevourmentPath, yours);
        world.Live.WriteText(KarmaPath, yours);
        return entry;
    }

    [Fact]
    public void The_settings_of_the_mod_asked_for_land_in_the_save_folder()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "bpDifficulty = -3.323608", "bpDifficulty = 0");

        var result = world.Library.AdoptSettings(entry, new[] { Devourment });

        Assert.True(result.Success);
        Assert.Equal("bpDifficulty = -3.323608", File.ReadAllText(world.Live.Resolve(DevourmentPath)));
    }

    [Fact]
    public void A_mod_that_was_not_asked_for_keeps_the_settings_you_had()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");

        world.Library.AdoptSettings(entry, new[] { Devourment });

        Assert.Equal("yours = 1", File.ReadAllText(world.Live.Resolve(KarmaPath)));
    }

    /// <summary>
    /// The point of the whole command: a save the settings came from is not a save that was loaded.
    /// </summary>
    [Fact]
    public void No_slot_is_written_at_all()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");

        var before = Hashing.ComputeFileSha256(world.Live.Resolve(LocalOne.FileName));
        var beforeTwo = Hashing.ComputeFileSha256(world.Live.Resolve(LocalTwo.FileName));

        world.Library.AdoptSettings(entry, new[] { Devourment, KarmaControl });

        Assert.Equal(before, Hashing.ComputeFileSha256(world.Live.Resolve(LocalOne.FileName)));
        Assert.Equal(beforeTwo, Hashing.ComputeFileSha256(world.Live.Resolve(LocalTwo.FileName)));
    }

    [Fact]
    public void The_entry_itself_is_left_alone()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");
        var stored = Hashing.ComputeFileSha256(Path.Combine(entry.DirectoryPath, LibraryEntry.SaveFileName));

        world.Library.AdoptSettings(entry, new[] { Devourment });

        Assert.Equal(
            stored,
            Hashing.ComputeFileSha256(Path.Combine(entry.DirectoryPath, LibraryEntry.SaveFileName)));
    }

    [Fact]
    public void A_safety_copy_is_taken_and_restoring_it_puts_the_old_settings_back()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");

        var result = world.Library.AdoptSettings(entry, new[] { Devourment });

        Assert.NotNull(result.SafetySnapshot);
        Assert.Equal("theirs = 1", File.ReadAllText(world.Live.Resolve(DevourmentPath)));

        world.Backups.RestoreBackup(result.SafetySnapshot!);

        Assert.Equal("yours = 1", File.ReadAllText(world.Live.Resolve(DevourmentPath)));
    }

    /// <summary>
    /// A settings file this machine has never had is created rather than skipped, and the snapshot
    /// that did not hold it is what deletes it again.
    /// </summary>
    [Fact]
    public void A_settings_file_you_never_had_is_created_and_the_undo_removes_it()
    {
        using var world = new LibraryWorld();
        const string OnlyTheirs = @"ModConfigs\theirmod.txt";

        world.Live.WriteText(OnlyTheirs, "theirs = 1");
        var entry = world.Library.StoreSlot(LocalTwo, "their save", null);
        File.Delete(world.Live.Resolve(OnlyTheirs));

        var result = world.Library.AdoptSettings(entry, new[] { "theirmod" });

        Assert.True(result.Success);
        Assert.True(File.Exists(world.Live.Resolve(OnlyTheirs)));

        world.Backups.RestoreBackup(result.SafetySnapshot!);

        Assert.False(File.Exists(world.Live.Resolve(OnlyTheirs)));
    }

    [Fact]
    public void Asking_for_nothing_writes_nothing_and_takes_no_safety_copy()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");
        var backupsBefore = world.Backups.ListBackups().Count;

        var result = world.Library.AdoptSettings(entry, Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(0, result.SettingsWritten);
        Assert.Null(result.SafetySnapshot);
        Assert.Equal(backupsBefore, world.Backups.ListBackups().Count);
        Assert.Equal("yours = 1", File.ReadAllText(world.Live.Resolve(DevourmentPath)));
    }

    [Fact]
    public void A_mod_the_entry_carries_nothing_for_writes_nothing()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");

        var result = world.Library.AdoptSettings(entry, new[] { "a.mod.nobody.has" });

        Assert.False(result.Success);
        Assert.Equal(0, result.SettingsWritten);
    }

    /// <summary>
    /// Devourment owns two files, its own .txt and the DvrmentConfs tree, so tampering with one
    /// leaves the other to land. The skipped one has to name itself.
    /// </summary>
    [Fact]
    public void A_settings_file_whose_checksum_rotted_is_skipped_and_named()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");

        // Rewritten inside the entry, so what is there no longer matches the recorded digest.
        var inside = Path.Combine(entry.DirectoryPath, LibraryEntry.ConfigsFolderName, "devourment.txt");
        File.WriteAllText(inside, "tampered = 1");

        var result = world.Library.AdoptSettings(world.Reload(entry), new[] { Devourment });

        Assert.Equal(1, result.SettingsWritten);
        Assert.Contains(result.Warnings, warning => warning.Contains("devourment.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("yours = 1", File.ReadAllText(world.Live.Resolve(DevourmentPath)));
    }

    [Fact]
    public void The_game_running_refuses_before_anything_is_taken()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");
        world.Detector.RunningProcessName = "RainWorld";

        Assert.Throws<GameRunningException>(() => world.Library.AdoptSettings(entry, new[] { Devourment }));
        Assert.Equal("yours = 1", File.ReadAllText(world.Live.Resolve(DevourmentPath)));
    }

    /// <summary>
    /// Two mods asked for, one of them broken. The good one still lands: a settings file that will
    /// not write is a warning, not a reason to put the other one back. Devourment counts twice,
    /// because its DvrmentConfs tree travels with its .txt.
    /// </summary>
    [Fact]
    public void One_file_that_will_not_write_does_not_stop_the_others()
    {
        using var world = new LibraryWorld();
        var entry = StoredWith(world, "theirs = 1", "yours = 1");

        var inside = Path.Combine(entry.DirectoryPath, LibraryEntry.ConfigsFolderName, "karmacontrol.txt");
        File.Delete(inside);

        var result = world.Library.AdoptSettings(world.Reload(entry), new[] { Devourment, KarmaControl });

        Assert.Equal(2, result.SettingsWritten);
        Assert.Equal("theirs = 1", File.ReadAllText(world.Live.Resolve(DevourmentPath)));
        Assert.Equal("yours = 1", File.ReadAllText(world.Live.Resolve(KarmaPath)));
    }
}
