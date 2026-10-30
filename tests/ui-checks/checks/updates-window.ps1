$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# Opening and closing the Updates window, which now cancels a queued install when it closes.
# The runner may or may not reach GitHub, so a failed update check is fine here.

Initialize-RwcProfile | Out-Null
Start-Rwc | Out-Null

Find-RwcElement -Name 'Updates' -ControlType Button -TimeoutSeconds 20 | Invoke-RwcElement
$updates = Find-RwcWindow -Title 'Updates' -TimeoutSeconds 20
Start-Sleep -Seconds 5
Save-RwcScreenshot -Name 'updates' -Window 'Updates' | Out-Null
Get-RwcTree -Scope $updates -Name 'updates-tree' | Out-Null

Find-RwcElement -Name 'Close' -ControlType Button -Scope $updates | Invoke-RwcElement
Wait-Rwc {
    try { Find-RwcWindow -Title 'Updates' -TimeoutSeconds 1 | Out-Null; $false } catch { $true }
} 'the Updates window closed' -TimeoutSeconds 15

try {
    $stray = Find-RwcWindow -Title '*' -Message -TimeoutSeconds 3
    Save-RwcDesktopScreenshot -Name 'unexpected-message' | Out-Null
    throw "unexpected message box after closing Updates: $((Get-RwcText $stray) -join ' ')"
}
catch { if ("$_" -like 'unexpected message box*') { throw } }

Assert-Rwc (Find-RwcElement -Name 'Copy Slot' -ControlType Button).Current.IsEnabled 'the main window is usable again'
Save-RwcScreenshot -Name 'main-after-updates' | Out-Null

$proc = Get-Process RainWorldCompanion
$proc.CloseMainWindow() | Out-Null
Wait-Rwc { $proc.HasExited } 'the app closes normally' -TimeoutSeconds 20
