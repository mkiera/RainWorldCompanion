$ErrorActionPreference = 'Stop'
Import-Module $env:RWC_UI_MODULE

# A damaged settings file must be kept as settings.json.corrupt-<time>, byte for byte, and the
# app must say so. Before the fix the file was replaced with defaults and nothing was kept.

$p = Initialize-RwcProfile
$settingsPath = Join-Path $p.AppData 'settings.json'

# Defaults turn telemetry on, so the runner must not reach the real endpoint.
if ($env:GITHUB_ACTIONS -eq 'true') {
    try {
        Add-Content -Path "$env:windir\System32\drivers\etc\hosts" -Value "`r`n127.0.0.1 rwc-telemetry.kiera.pet"
        ipconfig /flushdns | Out-Null
    }
    catch { Write-Host "could not block the telemetry host: $_" }
}

$valid = [IO.File]::ReadAllBytes($settingsPath)
$truncated = $valid[0..([int]($valid.Length / 2))]
[IO.File]::WriteAllBytes($settingsPath, [byte[]]$truncated)
$writtenHash = (Get-FileHash $settingsPath -Algorithm SHA256).Hash
Write-Host "wrote $($truncated.Length) of $($valid.Length) bytes, sha256 $writtenHash"

Start-Rwc | Out-Null

$box = Find-RwcWindow -Title 'Settings' -Message -Text '*could not be read*' -TimeoutSeconds 30
$text = (Get-RwcText $box) -join "`n"
Write-Host "message: $text"
Save-RwcDesktopScreenshot -Name 'settings-warning' | Out-Null
Assert-Rwc ($text -like '*A copy of it was kept at*') 'the warning says where the copy was kept'

$copies = @(Get-ChildItem $p.AppData -Filter 'settings.json.corrupt-*')
Assert-Rwc ($copies.Count -ge 1) "a settings.json.corrupt-* copy exists ($($copies.Name -join ', '))"
foreach ($c in $copies) {
    Assert-Rwc ((Get-FileHash $c.FullName -Algorithm SHA256).Hash -eq $writtenHash) "$($c.Name) is byte-identical to the damaged file"
}

Close-RwcMessage -Window $box -Button 'OK'

# With defaults and no Rain World on the runner, the app asks for the folders next. Cancel it.
try {
    $dialog = Find-RwcWindow -Title 'Settings' -TimeoutSeconds 15
    Save-RwcScreenshot -Name 'settings-dialog' -Window 'Settings' | Out-Null
    Find-RwcElement -Name 'Cancel' -ControlType Button -Scope $dialog | Invoke-RwcElement
}
catch { Write-Host "no Settings dialog to cancel: $_" }

Save-RwcScreenshot -Name 'main-after-warning' | Out-Null
Stop-Rwc
Wait-Rwc { -not (Get-Process RainWorldCompanion -ErrorAction SilentlyContinue) } 'the app exited' -TimeoutSeconds 20

# Closing writes settings.json again. The kept copy must outlive that.
$after = @(Get-ChildItem $p.AppData -Filter 'settings.json.corrupt-*')
Assert-Rwc ($after.Count -ge 1) 'the corrupt copy still exists after the app closed'
foreach ($c in $after) {
    Assert-Rwc ((Get-FileHash $c.FullName -Algorithm SHA256).Hash -eq $writtenHash) "$($c.Name) is still byte-identical after close"
}
Get-ChildItem $p.AppData | Format-Table Name, Length, LastWriteTime | Out-String | Write-Host
