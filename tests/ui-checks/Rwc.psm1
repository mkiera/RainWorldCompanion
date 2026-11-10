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

# UI Automation lists no children for a Win32 message box here, so its text and buttons are read
# and clicked through its window handle.
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class RwcMsg {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static List<object[]> Children(IntPtr parent) {
        var list = new List<object[]>();
        EnumChildWindows(parent, (h, l) => {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            var t = new StringBuilder(4096); GetWindowText(h, t, 4096);
            list.Add(new object[] { c.ToString(), t.ToString(), h });
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static void Click(IntPtr button) { PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
}
"@

$script:AE = [System.Windows.Automation.AutomationElement]
$script:Descendants = [System.Windows.Automation.TreeScope]::Descendants
$script:Proc = $null
$script:Shot = 0
$script:Detached = New-Object System.Collections.Generic.List[object]

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
        # Adds sav3.bin as slot 2 and options.bin, which carry one mod's records and name it in the
        # Mods window. Artifacts here are public, so leave it off unless a check is about that mod.
        [switch]$ModData,
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
            'sav2'       = $(if ($ModData) { 'sav3.bin' } else { 'sav2.bin' })
            'online_sav' = 'online_sav.bin'
            'expCore1'   = 'expCore1.bin'
            'exp1'       = 'exp1.bin'
        }
        if ($ModData) { $map['options'] = 'options.bin' }
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
        windowWidth              = 1600
        windowHeight             = 960
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
        # A window element from Find-RwcWindow, for a dialog that shares its title with another window.
        [System.Windows.Automation.AutomationElement]$Scope,
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
        $root = if ($Scope) { $Scope } else { Get-RwcWindow -Title $Window }
        $all = $root.FindAll($Descendants, $cond)
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
    param([string]$Window, [string]$Name = 'tree', [System.Windows.Automation.AutomationElement]$Scope)
    $scope = if ($Scope) { $Scope } else { Get-RwcWindow -Title $Window }
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

function Get-RwcWindowList {
    # Every top-level window of the app and every window nested under one, owned dialogs included.
    $proc = Get-RwcProcess
    $byPid = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    $isWindow = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($top in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $byPid)) {
        $list.Add($top)
        foreach ($w in $top.FindAll($Descendants, $isWindow)) { $list.Add($w) }
    }
    return , $list
}

function Get-RwcText {
    # The names of every text element under an element: a message box's message, a dialog's lines.
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Element)
    if ($Element.Current.ClassName -eq '#32770') {
        return @(foreach ($c in [RwcMsg]::Children([IntPtr]$Element.Current.NativeWindowHandle)) { if ($c[0] -eq 'Static' -and $c[1]) { $c[1] } })
    }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $names = @(foreach ($e in $Element.FindAll($Descendants, $cond)) { $e.Current.Name })
    return $names
}

function Find-RwcWindow {
    # A message box (Win32 class #32770) often carries the title of the dialog that raised it,
    # so -Message tells the two apart. -Text needs one text line matching the wildcard.
    param(
        [string]$Title = '*',
        [switch]$Message,
        [string]$Text,
        [int]$TimeoutSeconds = 20
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        foreach ($w in (Get-RwcWindowList)) {
            try {
                $c = $w.Current
                if ($c.Name -notlike $Title) { continue }
                if ($Message.IsPresent -ne ($c.ClassName -eq '#32770')) { continue }
                if ($Text -and -not @(Get-RwcText $w | Where-Object { $_ -like $Text }).Count) { continue }
                return $w
            }
            catch [System.Windows.Automation.ElementNotAvailableException] { }
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    $kind = if ($Message) { 'message box' } else { 'window' }
    throw "no $kind titled '$Title'$(if ($Text) { " with text '$Text'" })"
}

function Close-RwcMessage {
    # Clicks a message box button by its caption: OK, Yes, No, Cancel.
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Window, [string]$Button = 'OK')
    if ($Window.Current.ClassName -eq '#32770') {
        $h = [IntPtr]$Window.Current.NativeWindowHandle
        foreach ($c in [RwcMsg]::Children($h)) {
            if ($c[0] -eq 'Button' -and ($c[1] -replace '&', '') -eq $Button) {
                [RwcWin32]::SetForegroundWindow($h) | Out-Null
                [RwcMsg]::Click($c[2])
                Start-Sleep -Milliseconds 600
                return
            }
        }
        $all = @(foreach ($c in [RwcMsg]::Children($h)) { "$($c[0]):$($c[1])" }) -join ' | '
        throw "message box '$($Window.Current.Name)' has no '$Button' button. Children: $all"
    }
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $Window.FindAll($Descendants, $cond)) {
        if (($b.Current.Name -replace '&', '') -eq $Button) { Invoke-RwcElement $b; return }
    }
    throw "message box '$($Window.Current.Name)' has no '$Button' button"
}

function Invoke-RwcElementDetached {
    # For a control whose click handler opens a modal box before it returns, such as a menu item:
    # a plain Invoke can wait on that box. This one returns at once.
    param([Parameter(Mandatory, ValueFromPipeline)]$Element)
    process {
        $ps = [PowerShell]::Create()
        $null = $ps.AddScript({
            param($e)
            $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        }).AddArgument($Element)
        $null = $ps.BeginInvoke()
        $script:Detached.Add($ps)
        Start-Sleep -Milliseconds 600
    }
}

function Get-RwcAncestor {
    # The nearest ancestor of a control type, for a button whose only name is the text inside it.
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Element, [Parameter(Mandatory)][string]$ControlType)
    $ct = [System.Windows.Automation.ControlType]::$ControlType
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $e = $walker.GetParent($Element)
    while ($e -and $e.Current.ControlType -ne $ct) { $e = $walker.GetParent($e) }
    if (-not $e) { throw "no $ControlType above '$($Element.Current.Name)'" }
    return $e
}

function Select-RwcComboItem {
    # Opens a combo box, selects the item with that name and closes it again.
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$ComboBox, [Parameter(Mandatory)][string]$Item)
    $ec = Get-RwcPattern $ComboBox ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand()
    Start-Sleep -Milliseconds 500
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Item)
    $deadline = (Get-Date).AddSeconds(5)
    $found = $null
    do {
        $found = $ComboBox.FindFirst($Descendants, $cond)
        if (-not $found) { Start-Sleep -Milliseconds 250 }
    } while (-not $found -and (Get-Date) -lt $deadline)
    if (-not $found) {
        $names = @(foreach ($e in $ComboBox.FindAll($Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { $e.Current.Name }) -join ' | '
        throw "combo box '$($ComboBox.Current.Name)' has no item '$Item'. Items: $names"
    }
    (Get-RwcPattern $found ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 300
    try { $ec.Collapse() } catch { }
    Start-Sleep -Milliseconds 500
}

function Wait-Rwc {
    # Polls a script block until it returns something true. Fails the check with the message.
    param([Parameter(Mandatory)][scriptblock]$Until, [Parameter(Mandatory)][string]$Message, [int]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (& $Until) { Write-Host "ok: $Message"; return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "check failed after $TimeoutSeconds s: $Message"
}

function Get-RwcRecycleBin {
    # What the Recycle Bin of this user holds. Name is the original name, which can lack its extension.
    $bin = (New-Object -ComObject Shell.Application).Namespace(10)
    foreach ($i in @($bin.Items())) {
        [pscustomobject]@{
            Name        = $i.Name
            DeletedFrom = "$($i.ExtendedProperty('System.Recycle.DeletedFrom'))"
            Path        = $i.Path
        }
    }
}

Export-ModuleMember -Function *-Rwc*
