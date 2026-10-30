# UI checks

`.github/workflows/ui-check.yml` builds the app on a GitHub Windows runner, runs every script in
`checks/`, and uploads the screenshots, control lists and logs as the `ui-check-results` artifact.
The runner starts clean on every run. It has no Rain World install and no real settings, so a check
can write, delete and restore freely.

## Run it

Push a branch. The workflow runs on every push that changes more than Markdown files. To run only
some checks on a branch that is already pushed:

    gh workflow run ui-check.yml --ref <branch> -f filter=<name or wildcard>

`workflow_dispatch` works only once this file is on the default branch. Until then, push.

Read the result:

    gh run list --workflow ui-check.yml --branch <branch> --limit 1
    gh run watch <run id> --exit-status
    gh run download <run id> --name ui-check-results --dir <folder>

Each check gets a folder holding `log.txt`, numbered screenshots, any control lists it wrote and,
when it failed, `on-failure.png` of the whole screen. `summary.md` lists pass and fail.

## Write a check

Add `checks/<name>.ps1`. A check passes when it exits without an error.

    $ErrorActionPreference = 'Stop'
    Import-Module $env:RWC_UI_MODULE

    $p = Initialize-RwcProfile          # fresh settings and a staged save folder
    Start-Rwc | Out-Null
    Find-RwcElement -Name 'Library' -ControlType RadioButton | Select-RwcElement
    Get-RwcTree -Name 'library-tree' | Out-Null
    Save-RwcScreenshot -Name 'library' | Out-Null
    Assert-Rwc (Test-Path (Join-Path $p.Saves 'sav')) 'slot 1 save is still there'
    Stop-Rwc

`Initialize-RwcProfile` wipes `%LOCALAPPDATA%\RainWorldCompanion`, so it refuses to run outside
CI unless `RWC_UI_ALLOW_LOCAL=1` is set. Set that only in a throwaway Windows session.

The staged save folder holds the parser fixtures from `RainWorldCompanion.Core.Tests/Fixtures`
as `sav`, `sav2`, `online_sav`, `expCore1` and `exp1`, with `sav2.bin` in both slots.
`-ModData` puts `sav3.bin` in slot 2 and adds `options.bin`. Both carry one mod's records, and the
main window and the Mods window then show that mod's name. Artifacts of this public repository can
be downloaded, so use `-ModData` only for a check about that mod's data. Pass `-NoSaves` for an
empty folder, or `-Settings @{ theme = 'dark' }` to change a setting before launch.

| Function | Does |
| --- | --- |
| `Start-Rwc`, `Stop-Rwc` | Launch and wait for the main window. Close, then kill after 15 seconds |
| `Get-RwcWindow [-Title]` | The main window, or another window of the app by title |
| `Find-RwcElement -Name -Id -ControlType -Index -Window` | Waits up to 10 seconds for a control |
| `Invoke-RwcElement` | Clicks a button |
| `Select-RwcElement` | Selects a tab or list item. Tabs here are radio buttons |
| `Switch-RwcElement` | Toggles a checkbox |
| `Expand-RwcElement`, `Set-RwcText` | Expands an item, types into a text box |
| `Get-RwcTree [-Window] -Name` | Writes every named control, with enabled and off-screen state |
| `Save-RwcScreenshot -Name [-Window]` | One window, captured with `PrintWindow` |
| `Save-RwcDesktopScreenshot -Name` | The whole screen, for message boxes |
| `Assert-Rwc <condition> <message>` | Fails the check with the message |

WPF puts a templated item's view model type name in its Name, not in its AutomationId. Run
`Get-RwcTree` first to see what a window exposes.
