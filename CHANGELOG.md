# Changelog

What changed in each release, newest first. Headings are the version on its own,
without a leading `v`, and match the tag the release was built from. Work that
has not been released yet sits under Unreleased.

A pre-release takes a heading under its whole tag, as in `1.1.0-beta.1`. The
release build looks the heading up by the exact version it was tagged with and
fails when it finds none, so rename Unreleased before tagging, not after.

## Unreleased

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
