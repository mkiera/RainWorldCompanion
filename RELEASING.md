# Releases

Choose the version from the user-visible changes since the last valid stable
release. A release containing only bug fixes increments the patch number.
For example, fixes after 1.4.0 release as 1.4.1. A compatible new feature
increments the minor number and resets the patch number. An incompatible
change increments the major number and resets both other numbers. Tests,
refactors, documentation, and build changes alone do not justify a minor bump.

Check the choice before changing the version or tagging:

```powershell
./scripts/Get-ReleaseVersion.ps1 -PreviousStable 1.4.0 -ChangeType Fix -ProposedVersion 1.4.1
./scripts/Test-ReleaseVersion.ps1
./scripts/Test-BranchBuildVersion.ps1
```

Use the last valid stable release as the baseline when correcting a withdrawn
release. Keep the withdrawn tag and its frozen changelog section. A correction
can have a lower version than that tag. Add a withdrawn tag to
`.github/withdrawn-release-tags.txt` so branch builds exclude it when choosing
their version. Do not reuse or move published tags.

## Prepare and publish

1. Inspect main, beta, tags, release assets, workflows, and version fields.
   Classify the complete change set and check the version with the script.
2. Finish the changes and regression tests on a work branch. Merge work with
   multiple commits into beta with `git merge --no-ff`.
3. Set `Directory.Build.props` and add the exact version heading and date to
   `CHANGELOG.md`. Keep all published sections. A stable section collects the
   changes from its prereleases.
4. Run `dotnet test RainWorldCompanion.sln -c Release`. Check the staged paths,
   public wording, version, and release notes. Commit the prepared changes.
5. Merge beta into main once with `git merge --no-ff`. Put the stable tag on
   that merge. A beta tag belongs on its prepared beta commit.
6. Obtain the project owner's approval before any public push or release
   change. After approval, push the intended branches and tag atomically.
7. Follow the existing workflows through publication. Keep their triggers.
   Inspect the actual runs because branch and tag pushes can start separate
   builds. Verify the channel, asset, download digest, and packaged version.
8. Test the relevant update or downgrade in an isolated installation. Check
   shutdown, file replacement, relaunch, custom install location, settings,
   backups, library, and failure recovery. Record any untested parts.

## Correcting 1.5.0 to 1.4.1

1. Publish 1.4.1 and verify its installer before withdrawing 1.5.0.
2. Set 1.4.1 as GitHub Latest. Return the 1.5.0 release to draft while keeping
   its tag and assets. A prerelease flag alone still offers 1.5.0 on Beta.
3. Verify the public release list excludes 1.5.0 and that both Stable and Beta
   offer 1.4.1 to a 1.4.0 client. Verify a 1.4.1 client sees no 1.5.0 offer.
4. A 1.5.0 client must select 1.4.1 in Updates and confirm Downgrade, or run
   the 1.4.1 installer over its existing installation. Do not uninstall first.
   The code and save formats are unchanged. Its automatic update check only
   accepts higher semantic versions and does not read GitHub Latest.

Build metadata on 1.4.1 does not make it greater than 1.5.0. A new updater
shipped only in 1.4.1 cannot change the behavior of installed 1.5.0 clients.
Use the existing confirmed downgrade rather than a false version or a second
release with a higher number. Users who remain on 1.5.0 keep the same fixes,
but need this manual step to receive future 1.4.x patches automatically.
