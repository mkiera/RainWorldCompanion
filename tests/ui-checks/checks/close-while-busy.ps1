$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# Closing the window while a backup runs must be refused with a message, and the backup must
# finish. Before the fix the app closed and left the backup half written.
# A 2 GB file in a folder the backup takes whole keeps it busy for several seconds.

$p = Initialize-RwcProfile
$fillerDir = Join-Path $p.Saves 'Warp'
New-Item -ItemType Directory -Force -Path $fillerDir | Out-Null
$filler = Join-Path $fillerDir 'ui-check-filler.bin'
$size = 2GB
$fs = [IO.File]::Create($filler)
try { $fs.SetLength($size) } finally { $fs.Dispose() }
Write-Host "filler: $filler ($size bytes)"

try {
    Start-Rwc | Out-Null
    Wait-Rwc { (Find-RwcElement -Name 'New Backup' -ControlType Button -TimeoutSeconds 30).Current.IsEnabled } 'New Backup is enabled' -TimeoutSeconds 120
    Save-RwcScreenshot -Name 'main' | Out-Null

    Find-RwcElement -Name 'New Backup' -ControlType Button | Invoke-RwcElement
    $dialog = Find-RwcWindow -Title 'New backup' -TimeoutSeconds 20
    Find-RwcElement -Name 'Create' -ControlType Button -Scope $dialog | Invoke-RwcElement
    Start-Sleep -Seconds 1
    Save-RwcScreenshot -Name 'busy' | Out-Null

    $proc = Get-Process RainWorldCompanion
    $proc.CloseMainWindow() | Out-Null

    try {
        $box = Find-RwcWindow -Title 'RainWorld Companion' -Message -Text '*still working*' -TimeoutSeconds 20
    }
    catch {
        $alive = [bool](Get-Process RainWorldCompanion -ErrorAction SilentlyContinue)
        throw "no 'still working' message after closing during the backup (app running: $alive). If the backup finished first, the filler is too small: $_"
    }
    $text = (Get-RwcText $box) -join "`n"
    Write-Host "message: $text"
    Save-RwcDesktopScreenshot -Name 'close-refused' | Out-Null
    Assert-Rwc ($text -like '*Closing it now could leave a save half written*') 'the message says why the app stays open'
    Start-Sleep -Seconds 1
    Assert-Rwc (-not $proc.HasExited) 'the app is still running after the close request'
    Close-RwcMessage -Window $box -Button 'OK'

    Wait-Rwc { (Find-RwcElement -Name 'New Backup' -ControlType Button).Current.IsEnabled } 'the backup finished' -TimeoutSeconds 300
    Assert-Rwc (-not $proc.HasExited) 'the app is still running after the backup'

    $copied = @(Get-ChildItem (Join-Path $p.Root 'backups') -Recurse -File -Filter 'ui-check-filler.bin')
    Assert-Rwc ($copied.Count -eq 1 -and $copied[0].Length -eq $size) "the backup holds the whole filler ($($copied.FullName -join ', '))"
    Save-RwcScreenshot -Name 'after-backup' | Out-Null

    $proc.CloseMainWindow() | Out-Null
    Wait-Rwc { $proc.HasExited } 'the app closes on its own once it is idle' -TimeoutSeconds 20
}
finally {
    # Later checks run on the same runner disk.
    Get-Process RainWorldCompanion -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    Remove-Item -Recurse -Force $p.Root -ErrorAction SilentlyContinue
}
