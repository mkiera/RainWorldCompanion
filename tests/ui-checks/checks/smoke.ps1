$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

Initialize-RwcProfile | Out-Null
Start-Rwc | Out-Null

$win = Get-RwcWindow
Assert-Rwc ($win.Current.Name -like '*Companion*') "main window title is '$($win.Current.Name)'"

Get-RwcTree -Name 'main-tree' | Out-Null
Save-RwcScreenshot -Name 'main' | Out-Null

Find-RwcElement -Name 'Mods' -ControlType Button | Invoke-RwcElement
Get-RwcWindow -Title 'Mods' | Out-Null
Save-RwcScreenshot -Name 'mods' -Window 'Mods' | Out-Null
Save-RwcDesktopScreenshot -Name 'desktop' | Out-Null

Stop-Rwc
