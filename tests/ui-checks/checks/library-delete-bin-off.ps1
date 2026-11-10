$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# With the Recycle Bin turned off, Windows deletes outright without failing. Deleting a library
# save must then refuse and leave the folder in place.

$p = Initialize-RwcProfile
$library = Join-Path $p.Root 'library'
function Get-EntryFolders { @(Get-ChildItem $library -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'entry.json') }) }

# Windows keeps this per volume and creates the key on first use, so a fresh runner has none yet.
$bb = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket'
$guid = ((Get-CimInstance Win32_Volume -Filter "DriveLetter='$($env:SystemDrive)'").DeviceID -replace '^.*(\{[^}]+\}).*$', '$1')
$volumeKey = "$bb\Volume\$guid"
New-Item -Force -Path $volumeKey | Out-Null
Set-ItemProperty -Path $volumeKey -Name NukeOnDelete -Value 1 -Type DWord
Write-Host "bin turned off for $volumeKey"

Start-Rwc | Out-Null
Get-RwcTree -Name 'main-tree' | Out-Null

Find-RwcElement -Name 'Save slot to library' -ControlType Button -TimeoutSeconds 20 | Invoke-RwcElement
$dialog = Find-RwcWindow -Title 'Save * to the library' -TimeoutSeconds 20
Find-RwcElement -Name 'Name' -ControlType Edit -Scope $dialog | Set-RwcText -Value 'ui-check delete me'
Save-RwcScreenshot -Name 'save-slot-dialog' -Window $dialog.Current.Name | Out-Null
Find-RwcElement -Name 'Save it' -ControlType Button -Scope $dialog | Invoke-RwcElement

Wait-Rwc { (Get-EntryFolders).Count -eq 1 } 'the library holds one entry folder' -TimeoutSeconds 60
$entry = (Get-EntryFolders)[0]
Write-Host "entry folder: $($entry.FullName)"

Find-RwcElement -Name 'Library tab' -ControlType RadioButton | Select-RwcElement
Wait-Rwc { (Find-RwcElement -Name 'Delete' -ControlType Button).Current.IsEnabled } 'Delete is enabled with the new library save selected'
Save-RwcScreenshot -Name 'library-selected' | Out-Null
Get-RwcTree -Name 'library-tree' | Out-Null

Find-RwcElement -Name 'Delete' -ControlType Button | Invoke-RwcElement
$box = Find-RwcWindow -Title 'Delete library save' -Message -TimeoutSeconds 20
Close-RwcMessage -Window $box -Button 'Yes'

try { $err = Find-RwcWindow -Title '*' -Message -TimeoutSeconds 30 }
catch {
    $stillThere = Test-Path (Join-Path $entry.FullName 'entry.json')
    $inBin = @(Get-RwcRecycleBin | Where-Object { $_.Name -eq $entry.Name }).Count
    throw "no message after the delete. Folder still there: $stillThere. Items named $($entry.Name) in the Recycle Bin: $inBin"
}
$text = (Get-RwcText $err) -join "`n"
Write-Host "message: $text"
Save-RwcDesktopScreenshot -Name 'bin-off-refusal' | Out-Null
Assert-Rwc ($text -like '*Recycle Bin*') 'the app reports the Recycle Bin would not keep the save'
Close-RwcMessage -Window $err -Button 'OK'

Assert-Rwc (Test-Path (Join-Path $entry.FullName 'entry.json')) 'the entry folder is still in the library'
Assert-Rwc (@(Get-RwcRecycleBin | Where-Object { $_.Name -eq $entry.Name }).Count -eq 0) 'nothing of it is in the Recycle Bin'

Remove-Item -Recurse -Force $volumeKey

Save-RwcScreenshot -Name 'library-after-refusal' | Out-Null
Stop-Rwc
