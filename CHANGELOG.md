# Changelog

What changed in each release, newest first. Headings are the version on its own,
without a leading `v`, and match the tag the release was built from. Work that
has not been released yet sits under Unreleased.

A pre-release takes a heading under its whole tag, as in `1.1.0-beta.1`. The
release build looks the heading up by the exact version it was tagged with and
fails when it finds none, so rename Unreleased before tagging, not after.

A stable release collects what its pre-releases brought into one section,
worded as things ended up. The pre-release sections stay for their tags.

## Unreleased

## 1.2.1-beta.1 - 2026-08-29

- Going back to an older version and returning no longer loses the settings the
  newer one had added.

## 1.2.0 - 2026-08-28

- Turn Rain World's mods on and off from the app, without opening the game.
  The Mods window lists every mod you have with a tick beside it, and Apply
  writes what the game reads the next time it starts.
- Turning a mod on turns on the mods it needs, the way the game's own Remix
  menu does. One a mod needs that you do not have is named instead.
- The Mods window can match what a backup or library save was played with in
  one press, and put your own list back afterwards. Any mod you would rather
  leave alone stays where you put it.
- A mod a save needs but you do not have gets a button to its Steam Workshop
  page, and Refresh picks it up once you have subscribed. The link opens in
  Steam where it is installed, so Subscribe is there without signing in to
  the site.
- Restoring, loading or sending a save offers to turn its mods on there and
  then, instead of telling you to go and do it in the game's Remix menu. The
  Mods window opens over that one and hands you back to it when you close it.
  That covers mods you have turned on that the save never used, not just the
  ones it is missing.
- Writing a save whose mods do not match this machine now asks you to tick
  that you know, before the button will do anything.
- A library save and a `.rwsave` carry the mod settings that were on with
  them. Loading one asks which mods' settings to take, and takes none unless
  you tick them.
- Sending a campaign out of a backup or a library save offers its mod
  settings the same way.
- Take settings takes a library save's mod settings on their own, with no
  slot written and no campaign touched. It picks per mod the same way loading
  one does.
- Every place mod settings are listed says whether they are the same as the
  ones you have, different, or new to you. Ticking ones you already have says
  so before you apply them.
- The panel has a SETTINGS section under MODS, showing which mods' settings a
  save carries.
- Backups now take everything under ModConfigs, so a mod that keeps its
  settings in a folder or a .json is covered too.
- A backup or library save says which of its slots and campaigns differ from
  the ones in the game now. Opening a campaign marks the values that are not
  what the live slot holds.
- A whole slot can go to the library, every campaign in it at once, rather
  than only one campaign at a time. Save slot to library sits on the slot
  heading, in a backup and in the live save folder alike.
- Campaigns from Steam Workshop slugcat mods show the mod's own portrait
  instead of a grey placeholder, so Pearlcat and The DroneMaster look like
  themselves in the panel.
- Settings has a Dark mode tick that repaints the window and every dialog in
  dark colours. It is off unless you turn it on, and the choice is kept for
  next time.
- The window opens at a size that fits your screen instead of a fixed small
  one, and remembers any size or position you drag it to.
- The window shows which version is running, under the folder paths in the
  bottom left. Alpha builds also show their commit.
- The app tells you what changed the first time you run a new version, which
  used to silently never happen. The banner is one line with a count on it
  and a Read it button that opens the full list in its own window.
- The what's new banner covers every version between the one you last ran and
  the one you are on now, so skipping a release no longer skips what it
  changed.
- Check now in the Updates window records when it looked, so the line above
  it stops saying the last check was hours ago.
- Turning mods on to match a save before loading it no longer leaves the
  safety backup claiming the new mod list. It records the one you had before,
  so matching that backup's mods puts you back where you started.

## 1.2.0-beta.6 - 2026-08-28

- Turning a mod on in the Mods window turns on the mods it needs, the way the game's own
  Remix menu does. One a mod needs that you do not have is named instead.
- Campaigns from Steam Workshop slugcat mods show the mod's own portrait instead of a grey
  placeholder, so Pearlcat and The DroneMaster look like themselves in the panel.

## 1.2.0-beta.5 - 2026-08-28

- Settings has a Dark mode tick that repaints the window and every dialog in dark colours. It
  is off unless you turn it on, and the choice is kept for next time.

## 1.2.0-beta.4 - 2026-08-28

- Take settings takes a library save's mod settings on their own, with no slot written and
  no campaign touched. It picks per mod the same way loading one does.
- A whole slot in a backup can go to the library, every campaign in it at once, rather than
  only one campaign at a time. Save slot to library sits on the slot heading, in a backup and
  in the live save folder alike.
- Every place mod settings are listed says whether they are the same as the ones you have,
  different, or new to you. Ticking ones you already have says so before you apply them.
- A backup or library save says which of its slots and campaigns differ from the ones in the
  game now. Opening a campaign marks the values that are not what the live slot holds.

## 1.2.0-beta.3 - 2026-08-27

- The what's new banner covers every version between the one you last ran and the one
  you are on now, so skipping a release no longer skips what it changed.

## 1.2.0-beta.2 - 2026-08-27

- The what's new banner is one line with a count on it, and a Read it button that opens
  the full list in its own window, so a release with a lot in it no longer buries the app.
- Turn Rain World's mods on and off from the app, without opening the game. The
  Mods window lists every mod you have with a tick beside it, and Apply writes
  what the game reads the next time it starts.
- The Mods window can match what a backup or library save was played with in one
  press, and put your own list back afterwards. Any mod you would rather leave
  alone stays where you put it.
- A mod a save needs but you do not have gets a button to its Steam Workshop
  page, and Refresh picks it up once you have subscribed.
- Restoring, loading or sending a save offers to turn its mods on there and
  then, instead of telling you to go and do it in the game's Remix menu. The
  Mods window opens over that one and hands you back to it when you close it.
  That covers mods you have turned on that the save never used, not just the
  ones it is missing.
- Writing a save whose mods do not match this machine now asks you to tick that
  you know, before the button will do anything.
- A library save and a `.rwsave` carry the mod settings that were on with them.
  Loading one asks which mods' settings to take, and takes none unless you tick
  them.
- Sending a campaign out of a backup or a library save offers its mod settings
  the same way.
- The panel has a SETTINGS section under MODS, showing which mods' settings a
  save carries.
- Steam Workshop links open in Steam where it is installed, so Subscribe is
  there without signing in to the site.
- Check now in the Updates window records when it looked, so the line above it
  stops saying the last check was hours ago.
- Turning mods on to match a save before loading it no longer leaves the safety
  backup claiming the new mod list. It records the one you had before, so
  matching that backup's mods puts you back where you started.
- Backups now take everything under ModConfigs, so a mod that keeps its settings
  in a folder or a .json is covered too.
- The window shows which version is running, under the folder paths in the
  bottom left. Alpha builds also show their commit.
- The window opens at a size that fits your screen instead of a fixed small
  one, and remembers any size or position you drag it to.
- The app tells you what changed the first time you run a new version, which
  used to silently never happen.

## 1.1.0 - 2026-08-26

- Every backup and library save records the mods and game version it was
  played with, and carries that record inside a `.rwsave` when it is exported.
- The panel has a MODS section, and restoring, loading or sending a campaign
  says how this machine has moved since those bytes were written: what is
  missing, what is turned off, and what sits at another version. A missing
  workshop mod gets a link to its page. None of it blocks the operation.
- The app says what a version changed, once, the first time it runs on it.
- The Updates window shows a release's notes in place. The button that used to
  send you to the release page in a browser now opens them under the row, and
  the page is still one press away inside.
- Delete a save slot from the app. A slot can lose its campaigns or be emptied
  down to nothing, including a slot that holds data but no campaign.
- The food a run starts with reads correctly instead of showing a negative
  number.
- The Updates window calls its channels Beta and Alpha.

## 1.1.0-beta.1 - 2026-08-26

- Every backup and library save records the mods and game version it was
  played with, and carries that record inside a `.rwsave` when it is exported.
- The panel has a MODS section, and restoring, loading or sending a campaign
  says how this machine has moved since those bytes were written: what is
  missing, what is turned off, and what sits at another version. A missing
  workshop mod gets a link to its page. None of it blocks the operation.
- The app says what a version changed, once, the first time it runs on it.
- The Updates window shows a release's notes in place. The button that used to
  send you to the release page in a browser now opens them under the row, and
  the page is still one press away inside.
- Delete a save slot from the app. A slot can lose its campaigns or be emptied
  down to nothing, including a slot that holds data but no campaign.
- The food a run starts with reads correctly instead of showing a negative
  number.
- The Updates window calls its channels Beta and Alpha.

## 1.0.0 - 2026-08-25

- First release. Back up the Rain World save folder into dated snapshots, read
  what each slot holds, and restore any snapshot.
- Keep a library of named saves outside the game's three slots, and put any of
  them into any slot.
- Copy one slot onto another, and export or import a save as a single `.rwsave`
  file.
- Read Rain Meadow's online saves beside the local ones, and show what a
  Devourment campaign is carrying.
- The app was called Rain World Save Manager until this release and moves its
  own folders on first launch.
