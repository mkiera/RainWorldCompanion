$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# Replacing a saved mod list sends a copy of the old one to the Recycle Bin, and deleting one
# sends the list itself there. Before the fix both were lost for good. The runner has no mods,
# so the saved list is an empty one; what matters here is where the files go.

$p = Initialize-RwcProfile
$profiles = Join-Path $p.AppData 'modstate\profiles'
function Get-Profiles { if (Test-Path $profiles) { @(Get-ChildItem $profiles -Filter '*.json' -File) } else { @() } }
function Get-RecycledLists { @(Get-RwcRecycleBin | Where-Object { $_.Name -match '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}(\.json)?$' }) }

function Open-ProfileMenuItem([string]$item) {
    Find-RwcElement -Name 'More profile actions' -ControlType Button -Window 'Mods' | Invoke-RwcElement
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $item)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
    $found = $null
    $deadline = (Get-Date).AddSeconds(10)
    while (-not $found -and (Get-Date) -lt $deadline) {
        # The context menu is a popup, so it can sit outside the Mods window in the tree.
        foreach ($w in (Get-RwcWindowList)) {
            $found = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if ($found) { break }
        }
        if (-not $found) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $found) { throw "no menu item '$item'" }
    Save-RwcDesktopScreenshot -Name ("menu-" + ($item -replace '\W+', '-')) | Out-Null
    Invoke-RwcElementDetached $found
}

Start-Rwc | Out-Null

Find-RwcElement -Name 'Mods' -ControlType Button -TimeoutSeconds 20 | Invoke-RwcElement
Get-RwcWindow -Title 'Mods' | Out-Null
Find-RwcElement -Name 'Saved lists tab' -ControlType RadioButton -Window 'Mods' | Select-RwcElement
Save-RwcScreenshot -Name 'saved-lists' -Window 'Mods' | Out-Null
Get-RwcTree -Window 'Mods' -Name 'mods-tree' | Out-Null

# Save
Find-RwcElement -Name 'Save current list' -ControlType Button -Window 'Mods' | Invoke-RwcElement
$nameDialog = Find-RwcWindow -Title 'Save current list' -TimeoutSeconds 15
Find-RwcElement -Name 'Profile name' -ControlType Edit -Scope $nameDialog | Set-RwcText -Value 'ui-check list'
Find-RwcElement -Name 'Save profile' -ControlType Button -Scope $nameDialog | Invoke-RwcElement
Wait-Rwc { (Get-Profiles).Count -eq 1 } 'one saved list is on disk' -TimeoutSeconds 20
$profileFile = (Get-Profiles)[0]
$id = $profileFile.BaseName
$original = Get-Content -Raw $profileFile.FullName
Write-Host "saved list: $($profileFile.FullName)"
Save-RwcScreenshot -Name 'saved' -Window 'Mods' | Out-Null

# Replace
$recycledBefore = (Get-RecycledLists).Count
Open-ProfileMenuItem 'Replace with current ticks'
$box = Find-RwcWindow -Title 'Replace saved list' -Message -TimeoutSeconds 15
$text = (Get-RwcText $box) -join "`n"
Write-Host "message: $text"
Save-RwcDesktopScreenshot -Name 'replace-confirm' | Out-Null
Assert-Rwc ($text -like '*A copy of the old list goes to the Recycle Bin.*') 'the replace prompt mentions the Recycle Bin'
Close-RwcMessage -Window $box -Button 'Yes'

Wait-Rwc { (Get-RecycledLists).Count -eq $recycledBefore + 1 } 'the Recycle Bin got a copy of the old list' -TimeoutSeconds 20
Assert-Rwc (Test-Path $profileFile.FullName) 'the saved list itself is still there after Replace'
Assert-Rwc ((Get-Profiles).Count -eq 1) 'Replace left one saved list, not a second one'
Assert-Rwc ((Get-Content -Raw $profileFile.FullName) -ne $original) 'the saved list was rewritten by Replace'
Save-RwcScreenshot -Name 'replaced' -Window 'Mods' | Out-Null

# Delete
Open-ProfileMenuItem 'Delete'
$box = Find-RwcWindow -Title 'Delete saved list' -Message -TimeoutSeconds 15
$text = (Get-RwcText $box) -join "`n"
Write-Host "message: $text"
Save-RwcDesktopScreenshot -Name 'delete-confirm' | Out-Null
Assert-Rwc ($text -like '*Move the saved list "ui-check list" to the Recycle Bin?*') 'the delete prompt says the list goes to the Recycle Bin'
Close-RwcMessage -Window $box -Button 'Yes'

Wait-Rwc { -not (Test-Path $profileFile.FullName) } 'the saved list file is gone' -TimeoutSeconds 20
Wait-Rwc { @(Get-RwcRecycleBin | Where-Object { $_.Name -eq $id -or $_.Name -eq "$id.json" }).Count -ge 1 } "the Recycle Bin holds $id.json" -TimeoutSeconds 15
Assert-Rwc ((Get-RecycledLists).Count -eq $recycledBefore + 2) 'the Recycle Bin holds both old copies'
Get-RwcRecycleBin | Format-Table Name, DeletedFrom | Out-String | Write-Host

Save-RwcScreenshot -Name 'deleted' -Window 'Mods' | Out-Null
Stop-Rwc
