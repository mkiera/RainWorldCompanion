$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# Deleting a library save sends its folder to the Recycle Bin. Before the fix it was deleted
# outright.

$p = Initialize-RwcProfile
$library = Join-Path $p.Root 'library'
function Get-EntryFolders { @(Get-ChildItem $library -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'entry.json') }) }

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

$binBefore = @(Get-RwcRecycleBin)
Find-RwcElement -Name 'Delete' -ControlType Button | Invoke-RwcElement

$box = Find-RwcWindow -Title 'Delete library save' -Message -TimeoutSeconds 20
$text = (Get-RwcText $box) -join "`n"
Write-Host "message: $text"
Save-RwcDesktopScreenshot -Name 'delete-confirm' | Out-Null
Assert-Rwc ($text -like '*Move this library save to the Recycle Bin?*') 'the prompt says the save goes to the Recycle Bin'
Assert-Rwc ($text -like "*$($entry.Name)*") 'the prompt names the entry folder'
Close-RwcMessage -Window $box -Button 'Yes'

Wait-Rwc { -not (Test-Path $entry.FullName) } 'the entry folder is gone from the library folder' -TimeoutSeconds 30
Wait-Rwc { @(Get-RwcRecycleBin | Where-Object { $_.Name -eq $entry.Name }).Count -ge 1 } "the Recycle Bin holds $($entry.Name)" -TimeoutSeconds 15
$binAfter = @(Get-RwcRecycleBin)
Write-Host "Recycle Bin before: $($binBefore.Count) items, after: $($binAfter.Count)"
$binAfter | Format-Table Name, DeletedFrom | Out-String | Write-Host

try {
    $stray = Find-RwcWindow -Title '*' -Message -TimeoutSeconds 3
    Save-RwcDesktopScreenshot -Name 'unexpected-message' | Out-Null
    throw "unexpected message box: $((Get-RwcText $stray) -join ' ')"
}
catch { if ("$_" -like 'unexpected message box*') { throw } }

Save-RwcScreenshot -Name 'library-after-delete' | Out-Null
Stop-Rwc
