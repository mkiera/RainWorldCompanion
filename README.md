# Rain World Save Manager

A Windows desktop app that copies your Rain World save files into dated snapshots and restores
them on demand. It reads the save containers well enough to show what each slot holds, from the
slugcat and the cycle down to karma, passages, kills and Devourment state, so you can tell one
snapshot from another before you restore it.

This version does not edit saves. Files are copied byte for byte in both directions, because the
UTF-8 byte order mark and the trailing NUL padding the game writes are part of what the game
reads back.

## What it manages

The app copies, overwrites and deletes only these files, matched by exact name, and nothing else
in the save folder:

- `sav`, `sav2`, `sav3` (story slots 1 to 3)
- `exp<n>` and `expCore<n>` (expedition state, for example `exp1` and `expCore1`)
- `online_sav`, `online_sav2`, `online_sav3`
- `ModConfigs\devourment.txt`
- `dvrmentSaveStates\` and everything inside it, at any depth
- `ModConfigs\DvrmentConfs\` and everything inside it, at any depth

Everything else is left alone, including `options`, the game's own `backup\` and `cloud\` folders,
`steam_autocloud.vdf`, and other mods' config files.

The name match is exact and anchored. A live save folder often holds files such as `sav - Copy`,
`sav - Copy (2)` and `sav.bak` sitting next to `sav`, and a pattern such as `sav*` would pull
those in as files the app is allowed to overwrite and delete. They are out of scope.

Junctions and symlinks are out of scope too. If one of the folders above is a junction, or a file
inside it is a symlink, the app copies nothing through it and names what it skipped, in the backup
progress and again when you restore.

## The detail panel

The window is a list on the left and a detail panel on the right. The list holds the save folder
as it is right now and every backup under it. Selecting either fills the panel, and the layout is
the same for both, so a backup can be read against the live save without switching views.

A slot section lists its campaigns. Opening one shows:

- the run: cycle, cycles on this game version, food now, food eaten across the campaign,
  playtime, current and previous shelter, timeline and seed
- karma and the karma cap as the game stores them, the karma flower, and the flags for the mark
  of communication, the glow, ascension, beating the game, the citizen ID drone and Hunter's death
- deaths, survives and quits
- echoes met, gates unlocked, endgame passages, and the creatures the campaign has killed
- Devourment relationships with predator, prey, belly status and food value, plus swallowed items
  and anything held in hand

Karma is a 0-based index in the game, and it can sit above the cap. Both numbers are shown as the
save stores them and neither is adjusted.

A value the save did not record shows as a dash. Backups taken before this version recorded seven
fields per campaign, and their cards fill the rest in with dashes.

Passages are stored in more than one shape. Some hold a plain count, and the chip reads `x17`.
Others hold a float such as `30.29` or a dotted string such as `25.18.20`, which the app reads as
progress rather than as a number, and the chip shows that text with the full value in the tooltip.

Devourment works the same way. When the mod writes a relationship in a shape this app does not
read, the count on the campaign header still includes it and a line under the table says how many
were not read.

A backup's panel is filled from the manifest that was written with it, so selecting a backup
costs no disk read.

## Slugcat portraits

Campaigns and backup rows carry the face of the slugcat they belong to. The art is read from your
own Rain World install at runtime. None of it is copied into this repo or shipped with the app.

Settings can auto-detect the install from Steam, you can browse to it, or you can leave the field
blank. Where an install has no portrait for a slugcat, and no install has one for Inv, the app
draws a plain head in the slugcat's own colour.

The install path feeds the portraits and nothing else, so Settings does not validate it. A blank,
stale or wrong value costs a picture. Backups and restores run the same either way.

## Where things are stored

The save folder is detected at
`%USERPROFILE%\AppData\LocalLow\Videocult\Rain World`, and you can point the app somewhere else in
Settings.

Backups go to `%LOCALAPPDATA%\RainWorldSaveManager\backups` unless you change it. Each backup is
one folder named for the moment it was taken, for example `2026-08-24_19-31-07`, holding the
copied files in the same layout they have in the save folder plus a `manifest.json` listing every
file with its size and SHA-256.

The manifest is written last, so a folder without one is a backup that did not finish. The app
lists those as incomplete and refuses to restore them, and you can delete them from the list.

The backup folder must not be inside the save folder, and the save folder must not be inside
the backup folder. The app checks this by resolving both paths through the filesystem, so a
junction or a subst drive pointing one into the other is refused as well.

Settings live in `%LOCALAPPDATA%\RainWorldSaveManager\settings.json`.

## Close the game first

Backups and restores are refused while Rain World is running, because the game holds its
progression in memory and writes it back at its own save points. The header of the window tells
you whether the game is open, and the Backup and Restore buttons are disabled while it is.

The check is repeated during a restore. If the game starts while the restore is running, the
restore stops rather than writing more files under a process that is reading them, and tells you
the save folder is part restored.

## What a restore does

Restoring a backup makes the in-scope part of your save folder match that backup exactly. Before
you confirm, the app shows you which files will be added, which overwritten, which are already
identical, and which will be deleted.

**In-scope files that the backup does not contain are deleted.** If you started an expedition
after taking the backup, `exp1` and `expCore1` are not in it, and restoring removes them. That
is what makes a restore a return to one moment rather than a merge. Files outside the scope list
are left as they are.

The order is fixed:

1. Refuse if Rain World is running.
2. Refuse a backup that did not finish.
3. Re-hash every file inside the backup against its manifest, and refuse if anything has changed
   since it was taken.
4. Take a safety snapshot of the save folder as it is right now, and abandon the restore if that
   snapshot does not complete.
5. Check again that the game is closed, then copy the backup's files over the live ones.
6. Delete the in-scope live files the backup does not have, then remove folders inside
   `dvrmentSaveStates\` and `ModConfigs\DvrmentConfs\` that this left empty.
7. Re-hash the restored files and confirm they match the backup.

Step 6 only runs when every file in step 5 was copied, so a restore that failed to write your
saves back does not reach the step that deletes what is there now.

If a restore stops part way, the message says so and names the safety snapshot, which is the way
back. Restore that snapshot to return the save folder to how it was before you started.

## The safety snapshot

Every restore takes one automatically, just before it overwrites anything, and it is kept in the
backup list like any other backup, marked as an automatic pre-restore copy. It holds the save
folder exactly as it was at that moment, including files the backup you are restoring does not
have. Nothing is overwritten until that snapshot is on disk and complete.

Safety snapshots are not deleted automatically. They accumulate, and you can remove the ones you
no longer want from the list.

## Backup integrity

Every file copied into a backup is checked against the file it came from: same length, same
timestamp, and the same SHA-256 read back from both sides. A save that changes during the copy is
copied a second time, and if it changes again the backup stops without writing a manifest.

Recording only what arrived in the backup folder would produce a snapshot that agrees with
itself whatever happened to it. A save truncated by a cloud sync during the copy would be
recorded at its truncated length, with the hash of its truncated bytes, and every later check
would compare those bytes against themselves and pass.

Verify re-hashes a backup against its manifest whenever you ask, and a restore always does it
before touching anything.

## Steam Cloud

Rain World syncs its saves through Steam Cloud, which reads and writes the same files this app
does. Two habits keep them out of each other's way.

Close the game before backing up or restoring, and give Steam a moment to finish syncing before
you start. A backup that fails with a message about a file changing while it was copied usually
means a sync was still running. Wait for it and take the backup again.

After a restore, launch Rain World through Steam before you restart Steam. If a Steam Cloud
Conflict dialog appears, choose the option that keeps the local files, which is worded as uploading
to Steam Cloud. Choosing the cloud copy replaces the saves you just restored with the ones Steam
still has.

## Running two copies

Only one window can run at a time, and one backup or restore at a time can hold the backup folder.
A second one is refused with a message rather than being allowed to interleave, because two
operations writing the same save folder in the same second produce a snapshot that belongs to
neither of them.

## Building

Requires the .NET 9 SDK. The library and the tests target `net9.0`. The app targets
`net9.0-windows` and uses WPF, so it builds and runs on Windows only.

```
dotnet build RainWorldSaveManager.sln
dotnet test RainWorldSaveManager.sln
```

The tests read byte-exact save fixtures captured from a real installation and write only to
temporary directories. No test reads or writes a live save folder.
