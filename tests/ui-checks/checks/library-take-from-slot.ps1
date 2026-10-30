$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# A campaign saved to the library from slot 1 and put into slot 2 must be taken back from slot 2.
# The bug read slot 1, the slot it first came from. Undo take then trades the two generations,
# so a second Undo take brings the newer one back; before the fix the newer one was discarded.

$p = Initialize-RwcProfile
$library = Join-Path $p.Root 'library'

# Slot 2 starts as a well-formed save with no campaign, so the only campaign card is slot 1's
# and the put adds the campaign to slot 2 beside what is already there.
Copy-Item (Join-Path $p.Saves 'online_sav') (Join-Path $p.Saves 'sav2') -Force

function Get-Entry {
    $dirs = @(Get-ChildItem $library -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'entry.json') })
    if ($dirs.Count -ne 1) { return $null }
    try { $m = Get-Content -Raw (Join-Path $dirs[0].FullName 'entry.json') | ConvertFrom-Json } catch { return $null }
    [pscustomobject]@{ Dir = $dirs[0]; Manifest = $m }
}
function Show-Entry([string]$label) {
    $e = Get-Entry
    $m = $e.Manifest
    Write-Host "$label sha256=$($m.sha256) previousSha256=$($m.previousSha256) source=$($m.sourceFileName) previousSource=$($m.previousSourceFileName) lastPut=$($m.lastPutRealm)/$($m.lastPutSlot) lastLoaded=$($m.lastLoadedRealm)/$($m.lastLoadedSlot)"
    Get-ChildItem $e.Dir.FullName | ForEach-Object { Write-Host "  $($_.Name) $($_.Length)" }
}
function Confirm-Box([string]$title, [string]$expect, [string]$shot) {
    $box = Find-RwcWindow -Title $title -Message -TimeoutSeconds 30
    $text = (Get-RwcText $box) -join "`n"
    Write-Host "message '$title': $text"
    Save-RwcDesktopScreenshot -Name $shot | Out-Null
    Assert-Rwc ($text -like $expect) "the '$title' prompt reads like '$expect'"
    Close-RwcMessage -Window $box -Button 'Yes'
}

Start-Rwc | Out-Null
Get-RwcTree -Name 'main-tree' | Out-Null

# The campaign card's header button has no name of its own, only the slugcat's name inside it.
$main = Get-RwcWindow
$textCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$titles = @()
$deadline = (Get-Date).AddSeconds(20)
while (-not $titles.Count -and (Get-Date) -lt $deadline) {
    $titles = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) | Where-Object { $_.Current.Name -like '*Survivor*' })
    if (-not $titles.Count) { Start-Sleep -Milliseconds 500 }
}
Write-Host "Survivor texts: $($titles.Count)"
Assert-Rwc ($titles.Count -ge 1) 'slot 1 shows a Survivor campaign card'

# The live card on the left is a button too, and may name the slugcat, so skip it.
$opened = $false
foreach ($t in $titles) {
    try { $header = Get-RwcAncestor -Element $t -ControlType Button } catch { continue }
    if ($header.Current.Name -like 'Live save*') { continue }
    Invoke-RwcElement $header
    try { Find-RwcElement -Name 'Save to library' -ControlType Button -TimeoutSeconds 3 | Out-Null; $opened = $true; break } catch { }
}
Assert-Rwc $opened 'the Survivor campaign card opened and shows Save to library'
Save-RwcScreenshot -Name 'campaign-open' | Out-Null

Find-RwcElement -Name 'Save to library' -ControlType Button | Invoke-RwcElement
$dialog = Find-RwcWindow -Title 'Save * to the library' -TimeoutSeconds 20
Find-RwcElement -Name 'Name' -ControlType Edit -Scope $dialog | Set-RwcText -Value 'ui-check take'
Save-RwcScreenshot -Name 'save-campaign-dialog' -Window $dialog.Current.Name | Out-Null
Find-RwcElement -Name 'Save it' -ControlType Button -Scope $dialog | Invoke-RwcElement

Wait-Rwc { (Get-Entry) -ne $null } 'the library holds the campaign' -TimeoutSeconds 60
Show-Entry 'stored:'
Assert-Rwc ((Get-Entry).Manifest.sourceFileName -eq 'sav') 'the campaign was stored from sav'
$storedSha = (Get-Entry).Manifest.sha256

# Put in slot 2
Find-RwcElement -Name 'Library tab' -ControlType RadioButton | Select-RwcElement
Find-RwcElement -Name 'Put in slot' -ControlType Button | Invoke-RwcElement
$load = Find-RwcWindow -Title 'Load a library save' -TimeoutSeconds 30
Get-RwcTree -Scope $load -Name 'load-dialog-tree' | Out-Null
Select-RwcComboItem -ComboBox (Find-RwcElement -Name 'Slot to replace' -ControlType ComboBox -Scope $load) -Item 'Local slot 2  (sav2)'
Save-RwcScreenshot -Name 'put-in-slot-2' -Window 'Load a library save' | Out-Null
$loadButton = Find-RwcElement -Name 'Load' -ControlType Button -Scope $load
Assert-Rwc $loadButton.Current.IsEnabled 'Load is enabled for slot 2'
Invoke-RwcElement $loadButton

$box = Find-RwcWindow -Title 'Load a library save' -Message -TimeoutSeconds 60
Write-Host "load result: $((Get-RwcText $box) -join ' ')"
Save-RwcDesktopScreenshot -Name 'put-result' | Out-Null
Close-RwcMessage -Window $box -Button 'OK'
Show-Entry 'after put:'

# Take from slot
Find-RwcElement -Name 'Library tab' -ControlType RadioButton | Select-RwcElement
$listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$row = @((Get-RwcWindow).FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond) | Where-Object { $_.Current.Name -like 'Library save, ui-check take*' })[0]
Assert-Rwc ($null -ne $row) 'the library list shows ui-check take'
Select-RwcElement $row
Save-RwcScreenshot -Name 'library-selected' | Out-Null

Find-RwcElement -Name 'Take from slot' -ControlType Button | Invoke-RwcElement
Confirm-Box 'Take from slot' '*with what is in sav2 now?*' 'take-confirm'

Wait-Rwc { $e = Get-Entry; $e -and $e.Manifest.sourceFileName -eq 'sav2' -and $e.Manifest.previousSha256 } 'the take recorded sav2 and kept the earlier save' -TimeoutSeconds 60
Show-Entry 'after take:'
$takenSha = (Get-Entry).Manifest.sha256

# Undo take, twice
Wait-Rwc { (Find-RwcElement -Name 'Undo take' -ControlType Button).Current.IsEnabled } 'Undo take is enabled after the take'
Find-RwcElement -Name 'Undo take' -ControlType Button | Invoke-RwcElement
Confirm-Box 'Undo an update' '*Undo take brings it back*' 'undo-1-confirm'
Wait-Rwc { $e = Get-Entry; $e -and $e.Manifest.sourceFileName -eq 'sav' -and $e.Manifest.sha256 -eq $storedSha } 'the first Undo take went back to the save from sav' -TimeoutSeconds 60
Show-Entry 'after undo 1:'
Assert-Rwc ((Get-Entry).Manifest.previousSha256 -eq $takenSha) 'the taken save is kept as the earlier save'
Save-RwcScreenshot -Name 'after-undo-1' | Out-Null

Wait-Rwc { (Find-RwcElement -Name 'Undo take' -ControlType Button).Current.IsEnabled } 'Undo take is still enabled after the first undo'
Find-RwcElement -Name 'Undo take' -ControlType Button | Invoke-RwcElement
Confirm-Box 'Undo an update' '*Undo take brings it back*' 'undo-2-confirm'
Wait-Rwc { $e = Get-Entry; $e -and $e.Manifest.sourceFileName -eq 'sav2' -and $e.Manifest.sha256 -eq $takenSha } 'the second Undo take brought the save taken from sav2 back' -TimeoutSeconds 60
Show-Entry 'after undo 2:'
Save-RwcScreenshot -Name 'after-undo-2' | Out-Null

Stop-Rwc
