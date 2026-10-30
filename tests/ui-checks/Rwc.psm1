Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class RwcWin32 {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$script:AE = [System.Windows.Automation.AutomationElement]
$script:Descendants = [System.Windows.Automation.TreeScope]::Descendants
$script:Proc = $null
$script:Shot = 0

function Get-RwcOutDir {
    $dir = $env:RWC_UI_OUT
    if (-not $dir) { $dir = Join-Path ([IO.Path]::GetTempPath()) 'rwc-ui-out' }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Get-RwcDataRoot {
    $root = $env:RWC_UI_DATA
    if (-not $root) { $root = Join-Path ([IO.Path]::GetTempPath()) 'rwc-ui-data' }
    return $root
}

function Initialize-RwcProfile {
    # Deletes %LOCALAPPDATA%\RainWorldCompanion, so it refuses to run on a machine with a real install.
    param(
        [switch]$NoSaves,
        # Slot 2 becomes sav3.bin, which carries a mod's records. Its screenshots then show that
        # mod's name, so leave it off unless the check is about that mod's data.
        [switch]$ModSave,
        [hashtable]$Settings = @{}
    )

    if ($env:GITHUB_ACTIONS -ne 'true' -and $env:RWC_UI_ALLOW_LOCAL -ne '1') {
        throw 'Initialize-RwcProfile wipes the app data folder. It runs only on CI, or with RWC_UI_ALLOW_LOCAL=1 in a throwaway Windows session.'
    }

    $appData = Join-Path $env:LOCALAPPDATA 'RainWorldCompanion'
    if (Test-Path $appData) { Remove-Item -Recurse -Force $appData }
    New-Item -ItemType Directory -Force -Path $appData | Out-Null

    $root = Get-RwcDataRoot
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    $saves = Join-Path $root 'saves'
    $game = Join-Path $root 'game'
    foreach ($d in @($saves, (Join-Path $root 'backups'), (Join-Path $root 'library'), $game)) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }

    if (-not $NoSaves) {
        $fixtures = Join-Path $PSScriptRoot '..\RainWorldCompanion.Core.Tests\Fixtures'
        $map = [ordered]@{
            'sav'        = 'sav2.bin'
            'sav2'       = $(if ($ModSave) { 'sav3.bin' } else { 'sav2.bin' })
            'online_sav' = 'online_sav.bin'
            'expCore1'   = 'expCore1.bin'
            'exp1'       = 'exp1.bin'
            'options'    = 'options.bin'
        }
        foreach ($k in $map.Keys) {
            Copy-Item (Join-Path $fixtures $map[$k]) (Join-Path $saves $k)
        }
    }

    $s = [ordered]@{
        schemaVersion            = 1
        gameSavePath             = $saves
        backupRootPath           = (Join-Path $root 'backups')
        libraryRootPath          = (Join-Path $root 'library')
        gameInstallPath          = $game
        updateChannel            = 'stable'
        autoCheckUpdates         = $false
        theme                    = 'light'
        installId                = '00000000000000000000000000000000'
        telemetryEnabled         = $false
        lastSeenChangelogVersion = '999.0.0'
        windowWidth              = 1150
        windowHeight             = 720
        windowLeft               = 20
        windowTop                = 20
        windowMaximized          = $false
    }
    foreach ($k in $Settings.Keys) { $s[$k] = $Settings[$k] }
    $s | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $appData 'settings.json')

    [pscustomobject]@{ Root = $root; Saves = $saves; AppData = $appData }
}

function Start-Rwc {
    param([string[]]$Arguments = @(), [int]$TimeoutSeconds = 60)

    $exe = $env:RWC_UI_APP
    if (-not $exe -or -not (Test-Path $exe)) { throw "RWC_UI_APP does not point at the app: '$exe'" }

    $p = @{ FilePath = $exe; PassThru = $true }
    if ($Arguments.Count) { $p.ArgumentList = $Arguments }
    $script:Proc = Start-Process @p
    Get-RwcWindow -TimeoutSeconds $TimeoutSeconds | Out-Null
    # The window appears before the view model finishes loading the save folder.
    Start-Sleep -Seconds 3
    return $script:Proc
}

function Stop-Rwc {
    param([int]$TimeoutSeconds = 15)
    $procs = @(Get-Process RainWorldCompanion -ErrorAction SilentlyContinue)
    foreach ($p in $procs) { $p.CloseMainWindow() | Out-Null }
    foreach ($p in $procs) {
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill() }
    }
    $script:Proc = $null
}

function Get-RwcProcess {
    if ($script:Proc -and -not $script:Proc.HasExited) { return $script:Proc }
    $p = Get-Process RainWorldCompanion -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw 'RainWorld Companion is not running' }
    return $p
}

function Get-RwcWindow {
    param([string]$Title, [int]$TimeoutSeconds = 10)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $proc = Get-RwcProcess
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
        $main = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($main -and -not $Title) { return $main }
        if ($Title) {
            # Owned windows nest under the main window in the automation tree, and a few open
            # without an owner, so look in both places.
            $byTitle = New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Title)),
                (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
            $w = $null
            if ($main) { $w = $main.FindFirst($Descendants, $byTitle) }
            if (-not $w) {
                $all = $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
                foreach ($c in $all) { if ($c.Current.Name -eq $Title) { $w = $c; break } }
            }
            if ($w) { return $w }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    if ($Title) { throw "no window titled '$Title'" }
    throw "no main window for process $((Get-RwcProcess).Id)"
}

function Find-RwcElement {
    # WPF puts a view model's type name in Name for templated items, not in AutomationId.
    param(
        [string]$Name,
        [string]$Id,
        [string]$ControlType,
        [int]$Index = 0,
        [string]$Window,
        [int]$TimeoutSeconds = 10
    )

    $conds = @()
    if ($Name) { $conds += New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name) }
    if ($Id) { $conds += New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $Id) }
    if ($ControlType) {
        $ct = [System.Windows.Automation.ControlType]::$ControlType
        $conds += New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct)
    }
    if ($conds.Count -eq 0) { throw 'Find-RwcElement needs -Name, -Id or -ControlType' }
    $cond = if ($conds.Count -eq 1) { $conds[0] } else { New-Object System.Windows.Automation.AndCondition($conds) }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $scope = Get-RwcWindow -Title $Window
        $all = $scope.FindAll($Descendants, $cond)
        if ($all.Count -gt $Index) { return $all.Item($Index) }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "no element name='$Name' id='$Id' type='$ControlType' index=$Index in '$(if ($Window) { $Window } else { 'main window' })'"
}

function Get-RwcPattern($Element, $Pattern) {
    $obj = $null
    if (-not $Element.TryGetCurrentPattern($Pattern, [ref]$obj)) {
        throw "element '$($Element.Current.Name)' does not support $($Pattern.ProgrammaticName)"
    }
    return $obj
}

function Invoke-RwcElement {
    param([Parameter(ValueFromPipeline)]$Element)
    process { (Get-RwcPattern $Element ([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 400 }
}

function Select-RwcElement {
    # Tab strips in this app are radio buttons, so they take Select, not Invoke.
    param([Parameter(ValueFromPipeline)]$Element)
    process { (Get-RwcPattern $Element ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Start-Sleep -Milliseconds 400 }
}

function Switch-RwcElement {
    param([Parameter(ValueFromPipeline)]$Element)
    process { (Get-RwcPattern $Element ([System.Windows.Automation.TogglePattern]::Pattern)).Toggle(); Start-Sleep -Milliseconds 400 }
}

function Expand-RwcElement {
    param([Parameter(ValueFromPipeline)]$Element)
    process { (Get-RwcPattern $Element ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand(); Start-Sleep -Milliseconds 400 }
}

function Set-RwcText {
    param([Parameter(ValueFromPipeline)]$Element, [string]$Value)
    process { (Get-RwcPattern $Element ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Value); Start-Sleep -Milliseconds 200 }
}

function Get-RwcTree {
    # Writes every named control to <out>\<name>.txt and returns the lines.
    param([string]$Window, [string]$Name = 'tree')
    $scope = Get-RwcWindow -Title $Window
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::IsControlElementProperty, $true)
    $lines = foreach ($e in $scope.FindAll($Descendants, $cond)) {
        $c = $e.Current
        if ($c.Name -or $c.AutomationId) {
            '{0,-14} enabled={1,-5} offscreen={2,-5} id={3,-24} {4}' -f ($c.ControlType.ProgrammaticName -replace 'ControlType\.', ''), $c.IsEnabled, $c.IsOffscreen, $c.AutomationId, $c.Name
        }
    }
    $lines | Set-Content -Encoding utf8 (Join-Path (Get-RwcOutDir) "$Name.txt")
    return $lines
}

function Save-RwcScreenshot {
    # Captures one window with PrintWindow, so it works when the window is covered or off screen.
    param([Parameter(Mandatory)][string]$Name, [string]$Window)

    $el = Get-RwcWindow -Title $Window
    $h = [IntPtr]$el.Current.NativeWindowHandle
    [RwcWin32]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 600

    $r = New-Object RwcWin32+RECT
    [RwcWin32]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L
    $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    # 2 = PW_RENDERFULLCONTENT, without which WPF's DirectX surface comes out black.
    [RwcWin32]::PrintWindow($h, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc)
    $g.Dispose()
    $script:Shot++
    $path = Join-Path (Get-RwcOutDir) ('{0:D2}-{1}.png' -f $script:Shot, $Name)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "screenshot $path ($w x $ht)"
    return $path
}

function Save-RwcDesktopScreenshot {
    # The whole screen, for message boxes and anything outside the app's windows.
    param([Parameter(Mandatory)][string]$Name)
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $script:Shot++
    $path = Join-Path (Get-RwcOutDir) ('{0:D2}-{1}.png' -f $script:Shot, $Name)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "screenshot $path ($($b.Width) x $($b.Height))"
    return $path
}

function Assert-Rwc {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw "check failed: $Message" }
    Write-Host "ok: $Message"
}

Export-ModuleMember -Function *-Rwc*
