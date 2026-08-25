# RainWorld Companion

A Windows desktop app that copies your Rain World save files into dated snapshots and restores
them on demand. It reads the save containers well enough to show what each slot holds, from the
slugcat and the cycle down to karma, passages, kills and Devourment state, so you can tell one
snapshot from another before you restore it.

It also keeps a library of named saves outside the game's three slots, so you can keep twenty runs
and load any of them into any slot.

This version does not edit saves. Files are copied byte for byte in every direction, because the
UTF-8 byte order mark and the trailing NUL padding the game writes are part of what the game
reads back. It reads Rain Meadow's online saves with the same reader, pairs each one with the local
slot it shares a number with, and can copy any whole slot onto any other.

## Installing

Download `RainWorldCompanion-Setup.exe` from the
[releases page](https://github.com/mkiera/RainWorldCompanion/releases) and run it. It installs
for you alone under `%LOCALAPPDATA%\Programs\RainWorldCompanion`, so it asks for no admin
rights, and it starts the app when it finishes. The .NET runtime is inside the download, so
there is nothing to install first.

To update, run a newer setup over the top. It keeps your install location and your desktop
shortcut choice.

Uninstalling removes the program and its `settings.json`. **It does not touch your backups or
your save library.** Those are the only copy of those saves, and you can point either of them at
a folder of your own, so nothing here deletes them. Delete the folder yourself when you want it
gone.

Close the app before uninstalling. The uninstaller checks, and stops without removing anything if
it is still open, because a running copy holds files it would otherwise have to leave behind.

## What it manages

The app copies, overwrites and deletes only these files, matched by exact name, and nothing else
in the save folder:

- `sav`, `sav2`, `sav3` (story slots 1 to 3)
- `exp<n>` and `expCore<n>` (expedition state, for example `exp1` and `expCore1`)
- `online_sav`, `online_sav2`, `online_sav3` (Rain Meadow's online slots), and the
  `online_sav-<n>` names a lobby joined from an Expedition slot writes
- `meadow.json` (Rain Meadow character progression)
- `buffMain<n>` and `buffsave<n>` (RandomBuff save data)
- every `.txt` file sitting directly in `ModConfigs\`, which is where mods keep their settings
- `dvrmentSaveStates\`, `ModConfigs\DvrmentConfs\`, `dressmyslugcat\`, `RandomBuff\` and `Warp\`,
  with everything inside them at any depth

The rule behind that list is save data and mod configuration. Anything the game or Steam rewrites
on its own stays out of it:

- `options` and `localoptions.txt`, which hold resolution, keybinds and arena setup, and are
  rewritten every time you change one
- `SJ_0`, `SJ_1` and `SJ_2`, which hold karma cap screenshots the game redraws by itself and run to
  several megabytes
- `steam_autocloud.vdf` wherever it appears, including inside a folder the app otherwise takes
  whole, because putting a stale one back tells the Steam client that files it has already synced
  are current
- `cloud\`, which is Steam's, and `backup\`, which belongs to the game's own backup manager and
  runs to hundreds of megabytes

The name match is exact and anchored. A live save folder often holds files such as `sav - Copy`,
`sav - Copy (2)` and `sav.bak` sitting next to `sav`, and a pattern such as `sav*` would pull
those in as files the app is allowed to overwrite and delete. They are out of scope.

Junctions and symlinks are out of scope too. If one of the folders above is a junction, or a file
inside it is a symlink, the app copies nothing through it and names what it skipped, in the backup
progress and again when you restore.

## The detail panel

The window is a list on the left and a detail panel on the right. The list holds the save folder
as it is right now and every backup under it. Selecting either fills the panel, and the layout is
the same for both, so a backup can be read against the live save without switching views. With Rain
Meadow installed, a toggle above the sections picks which set of saves they show, local or online.

A slot section lists its campaigns. Opening one shows:

- the run: cycle, cycles on this game version, food now, food eaten across the campaign,
  playtime, current and previous shelter, timeline and seed
- karma, the karma cap, the karma flower, and the flags the record carries: the mark of
  communication, the glow, ascension, the citizen ID drone and whether the next cycle skips food
  drain. Hunter's campaign adds Hunter's death
- deaths, survives and quits
- echoes met and whether each was spoken to or only sensed, gates unlocked, endgame passages, and
  the creatures the campaign has killed
- Devourment contents as a tree, with belly status and food value, plus swallowed items and
  anything held in hand

Karma is shown the way the game shows it. The save holds a 0-based index that Rain World clamps to
the cap the moment it loads the file, so the panel clamps it the same way and then counts from 1.
The stored numbers are in the tooltip. A value outside 0 to the cap is ordinary: karma one above
the cap turns up in saves the game reads fine, and the void sea ascension writes -1. Both are
marked with an asterisk and explained on hover rather than reported as damage.

Hunter's card counts cycles down. The game's map and its save select screen both show that campaign
the cycles it has left, and the stored number is in the tooltip.

A value the save did not record shows as a dash. Backups taken before this version recorded seven
fields per campaign, and their cards fill the rest in with dashes.

A passage chip shows progress against what the passage needs, such as `5 / 5` or `1 / 3`. Green is
a passage the run has earned and not yet spent, which is the one the game offers at a shelter, and
slate is one already spent. The save records the spent flag and the progress separately, and
neither on its own says whether the passage is available, so the chip reads both. A passage added
by a mod has no requirement this app knows, so its chip carries the stored text on its own.

Devourment contents nest. The mod records one predator and prey pair per line, so a lizard that
was swallowed while itself holding a spear is written twice, once as prey and once as predator,
under the same entity id. The panel follows those ids and draws the chain, so the spear sits
inside the lizard inside whatever ate it. Rows with something inside them can be folded shut.

The outermost entry is whatever nothing else in the save is holding. That is usually the player,
but when the player has been eaten it is the predator, and the player and everything it was
carrying hang underneath. The flat list this replaced gave no way to tell those two apart.

Each row carries what the save knows about that entity. A pearl shows its type and the colour the
game paints it, so one lore pearl is told from another at a glance, and Five Pebbles' own pearls
show their number instead, which is the only thing that separates them. A creature shows how it
feels about you, out of its own social memory, and a spear says so when it is explosive, electric,
a needle, or poisoned. A creature something has already eaten from shows the meat it has left.

A creature on the campaign's friends list is marked tamed. That list is the game's own record of
which creatures it keeps with you between cycles, and it is deliberately shown apart from the
feeling beside it, because the two disagree. A creature can like you completely and not be on the
list: in one campaign here a moth and a vulture both sit at the maximum and neither is tamed.

Backups taken before this version recorded no entity ids, so their contents cannot be linked and
are drawn flat. A relationship written in a shape this app does not read still counts towards the
number on the campaign header, and a line under the tree says how many were not read.

A backup's panel is filled from the manifest that was written with it, so selecting a backup
costs no disk read.

## Rain Meadow

Rain Meadow keeps a second save for each slot. It hooks the method the game uses to name the save
file and returns `online_sav` where the game would have said `sav`, off the same slot number, so
slot 2 is `sav2` on your own and `online_sav2` in a lobby. The two files sit side by side in the
save folder and are the same format byte for byte, which is why one reader handles both. Expedition
is not hooked, so online play is story mode.

A SHOWING toggle above the slot sections switches them between the three local saves and the three
online ones. Both hold full campaigns, so both get the same sections and the same campaign cards,
down to the Devourment tree. An online section names its realm in the header, because `sav2` and
`online_sav2` share a slot number and the number alone does not say which one is on screen. The
toggle stays where you put it as you move between the live save and backups.

The paired rows lower down are the other half of it, and they stay as they are whichever way the
toggle is set: local and online in one row per slot number, all three rows always, so an empty
online slot is visible rather than absent.

The toggle and the paired section both appear when Rain Meadow is on the machine and are left out
entirely otherwise, so a player who does not use the mod sees one set of saves and no toggle over
them. Presence is read from the game's own enabled mod list when the game folder is known, and
otherwise from the save folder, which is enough on its own: `meadow.json`, the mod's Remix config
and the online saves are written by nothing else.

Rain Meadow records the map you have explored and a progression record whether or not a campaign is
saved, so an online save can hold 12 KB of real progress with no campaign in it. The panel describes
that as map and progression data. A slot that has never been played holds nothing and says so.

## Copying a slot

Copy Slot in the top bar picks both ends: a source and a target, from the three local slots and,
when Rain Meadow is on the machine, the three online ones. Any of them can be copied onto any
other, so local slot 1 onto online slot 3 is as available as local slot 1 onto local slot 2.
Changing either picker re-describes the copy from a fresh plan, so what the confirmation names is
what will run. Picking one file on both sides is refused, in the same words the copy itself would
use.

A copy replaces the whole target file byte for byte and takes a safety snapshot of the save folder
first, the same kind a restore takes, so the file it overwrote can be put back by restoring that
snapshot. The operation is a file copy and nothing more, so the byte order mark, the padding and the
MD5 inside the payload all arrive unchanged and the game reads the result as the file it came from.
The confirmation names both files, their sizes and what is in each, and the copy is refused if the
safety snapshot does not hold the file that is about to be replaced.

Moving one campaign between slots is not in this version. That means rewriting the payload and
recomputing the MD5 the game checks it against, and getting that wrong is what destroys a save. It
belongs with the save editor.

## The library

Rain World gives you three slots. The library is a folder of named saves outside them, so a run you
want to come back to does not have to hold a slot open.

Store Slot in the top bar keeps a copy of one slot under a name of your choosing. The copy is
proved against the file it came from before it is recorded, so a save the game rewrote mid-copy
abandons the entry rather than being stored as though it were sound. Nothing in the save folder is
written.

The LIBRARY tab in the left column lists what you have stored, with the same faces, campaign counts
and check state the backup rows carry. Selecting one fills the same detail panel a backup does.

A stored save reads the same whichever slot it came from. One taken from `online_sav2` lays out
exactly like one taken from `sav2`, and neither gets the Rain Meadow section or the local and online
toggle, both of which work across a slot's two halves and a single stored save has no second half.
Where it came from is on the row and in the panel subtitle.

The two buttons that move bytes name the direction they move them.

- **Put in slot** writes a library save into whichever slot you pick, local or online.
- **Take from slot** replaces a library save with what is in that slot now, which is how an hour of
  play gets back into the save it came from. The save being replaced is kept, so **Undo take** puts
  it back. Only the last one is kept, and the next take replaces it.
- **Rename** changes the name and the note and nothing else.
- **Export** writes one save out as a single `.rwsave` file. **Import** reads one back, and also
  accepts a bare save file copied straight out of somebody's save folder.

A row says which slot holds it and whether that slot has been played since. Both putting a save in a
slot and taking one back leave the save and the slot holding the same bytes, so either one starts
that badge fresh.

One slot has one library save on it. Putting a second save into a slot takes the badge off the first,
so two rows can never both claim to be in `sav`, and a restore or a slot copy takes it off whatever
it wrote over. Without a badge, the app is not claiming to know what is in that slot.

The time on a row is when the bytes were last written, so a save you have just taken from a slot
reads as minutes old rather than as old as the day you first stored it, and it moves to the top of
the list.

### What loading does

Loading is the only thing the library does that writes into the save folder, and it runs the same
steps a slot copy runs, in the same order: take a safety snapshot of the whole save folder, prove
that snapshot holds the file about to be replaced, hold the backup folder for the rest of the
operation, check again that the game is closed and that nothing has appeared in the target slot,
then one byte for byte copy, then hash both sides and compare.

The entry carries the SHA-256 recorded when it was stored, and the load holds it to that digest
immediately before the write. A library save damaged since it was stored is refused rather than
written over a live slot.

### Bundles and bare files

A `.rwsave` file is a zip holding the save and the manifest that describes it, which is what carries
the name, the note and the campaigns to another machine. Because the manifest records the save's
checksum, a bundle whose save no longer matches it has been damaged in transit and is refused.

A bare save file has no recorded checksum to hold it to, so a damaged one is imported with a warning
instead. A save the game will not load is still one you may want in the library to look at.

Neither writes into the save folder. An imported file lands in the library, and the only way it
reaches a slot is by being loaded, which takes a safety snapshot first.

### Naming

Entry folders are named for the time they were stored, for example `2026-08-24_19-31-07`. Your name
for a save lives in its `entry.json` and never in a path, so it can be anything: reserved names such
as `CON`, characters Windows refuses in a file name, two saves called the same thing. Renaming
rewrites the manifest and leaves the folder where it is.

`meadow.json` is Rain Meadow's own progression file, and the panel reads it: the character picked in
the menu, play time, progress towards the next emote, skin and character, and per character the
unlocked emotes and skins, the chosen skin and tint, the emote wheel, and the room it last saved in.
Play time is stored in milliseconds, added at 1000 divided by the frame rate once per game update,
so it runs slightly short of real time at a frame rate that does not divide 1000. The file belongs
to the mod and its shape can change with an update, so anything the app cannot make sense of is
reported in place rather than treated as damage.

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

Backups go to `%LOCALAPPDATA%\RainWorldCompanion\backups` unless you change it. Each backup is
one folder named for the moment it was taken, for example `2026-08-24_19-31-07`, holding the
copied files in the same layout they have in the save folder plus a `manifest.json` listing every
file with its size and SHA-256.

The manifest is written last, so a folder without one is a backup that did not finish. The app
lists those as incomplete and refuses to restore them, and you can delete them from the list.

Library saves go to `%LOCALAPPDATA%\RainWorldCompanion\library` unless you change it. Each one is
a folder named the same way, holding `save.bin`, an `entry.json` written last for the same reason,
and a `save.previous.bin` once an update has replaced something.

None of the three folders may sit inside another. The app checks this by resolving every path
through the filesystem, so a junction or a subst drive pointing one into another is refused as well.

Settings live in `%LOCALAPPDATA%\RainWorldCompanion\settings.json`.

This app was called Rain World Save Manager until August 2026, and kept the same three things in
`%LOCALAPPDATA%\RainWorldSaveManager`. On first launch after the rename it renames that folder,
which is a rename within one drive rather than a copy, so it takes the same moment whatever is
inside. Stored paths that pointed into the old folder are updated to match, and a backup or
library folder you pointed somewhere else yourself is left exactly where you put it.

If the rename cannot happen, because a file inside is open in another program, the app keeps
working from the old folder and tries again next time. Nothing is deleted either way.

## Close the game first

Anything that reads or writes a save file is refused while Rain World is running, because the game
holds its progression in memory and writes it back at its own save points. That covers backups,
restores, slot copies, and storing, updating and loading a library save. The header of the window
tells you whether the game is open and those buttons are disabled while it is.

The check is repeated during a restore or a copy. If the game starts while one is running, it stops
rather than writing more files under a process that is reading them, and tells you the save folder
is part written.

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
6. Delete the in-scope live files the backup does not have, then remove the folders that left
   empty.
7. Re-hash the restored files and confirm they match the backup.

Step 6 only runs when every file in step 5 was copied, so a restore that failed to write your
saves back does not reach the step that deletes what is there now.

Step 6 removes only the folders that restore emptied, and only inside the folders the app takes
whole. A folder that was already empty before the restore stays where it is, and so does the top of
each of those folders, which the mod that owns it expects to find.

If a restore stops part way, the message says so and names the safety snapshot, which is the way
back. Restore that snapshot to return the save folder to how it was before you started.

### Restoring a backup taken before this version

The list of managed files grew in this version, and a restore deletes managed files the backup does
not have, so widening that list widens what a restore can delete. A backup taken before this version
holds no `meadow.json`, no RandomBuff save data, no mod configs and none of the three folders added
above, and a restore that read those absences as instructions would remove all of them.

Every backup records which version of the rules decided its contents, and a restore deletes a file
only when both today's rules and the backup's own rules covered it. A backup written before that
version was recorded counts as the first one. Restoring a backup from before this version therefore
leaves the newly covered files as they are, and the confirmation lists them under a heading that
says so, beside the files that will be deleted.

An exclusion added since a backup was taken cuts the other way. `steam_autocloud.vdf` is left out
now wherever it appears, so an older backup holding one does not write it back. The confirmation
lists those too. Either list means the save folder will not match the backup exactly, which is why
both are on screen before you confirm.

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

Every backup is re-hashed against its own manifest without being asked. The check runs in the
background after the list is read, one snapshot at a time off the main thread, and each row's state
fills in as its turn finishes, so the list is usable while it works. A refresh cancels a check still
running, because its rows have already been replaced.

That is about knowing which backup is sound before you need one. A restore verifies the snapshot for
itself immediately beforehand either way, so it never depends on the background check having reached
that row.

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
dotnet build RainWorldCompanion.sln
dotnet test RainWorldCompanion.sln
```

The tests read byte-exact save fixtures captured from a real installation and write only to
temporary directories. No test reads or writes a live save folder.
