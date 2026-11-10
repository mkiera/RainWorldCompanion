$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# Copy Slot now writes through the hash-checked copy instead of File.Copy. A normal copy must
# still land byte for byte, keep the old target in a safety snapshot, and report no
# "changed after the safety copy" refusal.

$p = Initialize-RwcProfile
$source = Join-Path $p.Saves 'sav'
$target = Join-Path $p.Saves 'sav2'

# Both slots start as the same fixture. The online fixture is a well-formed save with no
# campaign, so the target differs from the source before the copy.
Copy-Item (Join-Path $p.Saves 'online_sav') $target -Force
$sourceHash = (Get-FileHash $source -Algorithm SHA256).Hash
$oldTargetHash = (Get-FileHash $target -Algorithm SHA256).Hash
Assert-Rwc ($sourceHash -ne $oldTargetHash) 'slot 1 and slot 2 differ before the copy'

Start-Rwc | Out-Null
Save-RwcScreenshot -Name 'main' | Out-Null

Find-RwcElement -Name 'Copy Slot' -ControlType Button -TimeoutSeconds 20 | Invoke-RwcElement
$dialog = Find-RwcWindow -Title 'Copy save slot' -TimeoutSeconds 20
Get-RwcTree -Scope $dialog -Name 'copy-dialog-tree' | Out-Null

if (-not @(Get-RwcText $dialog | Where-Object { $_ -eq 'Copy sav onto sav2?' }).Count) {
    Select-RwcComboItem -ComboBox (Find-RwcElement -Name 'Copy from' -ControlType ComboBox -Scope $dialog) -Item 'Local slot 1  (sav)'
    Select-RwcComboItem -ComboBox (Find-RwcElement -Name 'Replaces' -ControlType ComboBox -Scope $dialog) -Item 'Local slot 2  (sav2)'
}
Assert-Rwc (@(Get-RwcText $dialog | Where-Object { $_ -eq 'Copy sav onto sav2?' }).Count -eq 1) 'the dialog copies sav onto sav2'
Save-RwcScreenshot -Name 'copy-dialog' -Window 'Copy save slot' | Out-Null

$copy = Find-RwcElement -Name 'Copy' -ControlType Button -Scope $dialog
Assert-Rwc $copy.Current.IsEnabled 'Copy is enabled'
Invoke-RwcElement $copy

$box = Find-RwcWindow -Title 'Copy save slot' -Message -TimeoutSeconds 60
$text = (Get-RwcText $box) -join "`n"
Write-Host "message: $text"
Save-RwcDesktopScreenshot -Name 'copy-result' | Out-Null
Assert-Rwc ($text -notlike '*changed after the safety copy*') 'the copy was not refused as changed since the safety copy'
Assert-Rwc ($text -like '*Safety snapshot*') 'the result names a safety snapshot'
Close-RwcMessage -Window $box -Button 'OK'

Assert-Rwc ((Get-FileHash $target -Algorithm SHA256).Hash -eq $sourceHash) 'sav2 now equals sav byte for byte'
Assert-Rwc ((Get-FileHash $source -Algorithm SHA256).Hash -eq $sourceHash) 'sav is unchanged'

$kept = @(Get-ChildItem (Join-Path $p.Root 'backups') -Recurse -File -Filter 'sav2' |
    Where-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash -eq $oldTargetHash })
Assert-Rwc ($kept.Count -ge 1) "a safety backup holds the old sav2 ($($kept.FullName -join ', '))"

Save-RwcScreenshot -Name 'main-after-copy' | Out-Null
Stop-Rwc
