$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# Autosync on slot 1 copies the newer of sav and online_sav onto the other, takes a backup first,
# and then follows a new save written to online_sav while the app stays open.

$p = Initialize-RwcProfile
$local = Join-Path $p.Saves 'sav'
$online = Join-Path $p.Saves 'online_sav'

# sav is newer, so turning autosync on copies it onto online_sav.
(Get-Item $online).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddHours(-1)
$localHash = (Get-FileHash $local -Algorithm SHA256).Hash
$oldOnlineHash = (Get-FileHash $online -Algorithm SHA256).Hash
Assert-Rwc ($localHash -ne $oldOnlineHash) 'sav and online_sav differ before autosync'

Start-Rwc | Out-Null

function Show-Element($Element) {
    $obj = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$obj)) {
        $obj.ScrollIntoView()
        Start-Sleep -Milliseconds 500
    }
}

function Wait-Hash($Path, $Hash, $What) {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ((Get-FileHash $Path -Algorithm SHA256).Hash -eq $Hash) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "timed out waiting for $What"
}

$switch = Find-RwcElement -Name 'Autosync SLOT 1' -ControlType CheckBox -TimeoutSeconds 20
Show-Element $switch
Get-RwcTree -Name 'before-tree' | Out-Null
Save-RwcScreenshot -Name 'autosync-off' | Out-Null

Switch-RwcElement $switch
Wait-Hash $online $localHash 'online_sav to match sav'
Start-Sleep -Seconds 3
Show-Element (Find-RwcElement -Name 'Autosync SLOT 1' -ControlType CheckBox)
Get-RwcTree -Name 'after-enable-tree' | Out-Null
Save-RwcScreenshot -Name 'autosync-on-first-sync' | Out-Null

$kept = @(Get-ChildItem (Join-Path $p.Root 'backups') -Recurse -File -Filter 'online_sav' |
    Where-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash -eq $oldOnlineHash })
Assert-Rwc ($kept.Count -ge 1) 'a backup holds the online_sav that autosync replaced'

# online_sav replaced by a save with no campaign, as if written elsewhere. Copying it onto sav
# would drop sav's campaign, so autosync leaves both files alone and says why.
$fixtures = Join-Path $PSScriptRoot '..\..\RainWorldCompanion.Core.Tests\Fixtures'
Copy-Item (Join-Path $fixtures 'online_sav.bin') $online -Force
Start-Sleep -Seconds 8
Assert-Rwc ((Get-FileHash $local -Algorithm SHA256).Hash -eq $localHash) 'sav keeps its campaign'
Show-Element (Find-RwcElement -Name 'Autosync SLOT 1' -ControlType CheckBox)
$texts = Get-RwcTree -Name 'after-conflict-tree'
Save-RwcScreenshot -Name 'autosync-refused' | Out-Null
Assert-Rwc (@($texts | Where-Object { $_ -like '*did not copy over it*' }).Count -ge 1) 'the slot says why it did not sync'

Stop-Rwc

# Started again, the switch is still on.
Start-Rwc | Out-Null
$switch = Find-RwcElement -Name 'Autosync SLOT 1' -ControlType CheckBox -TimeoutSeconds 20
$state = (Get-RwcPattern $switch ([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState
Assert-Rwc ($state -eq 'On') "autosync is still on after a restart ($state)"
Stop-Rwc
