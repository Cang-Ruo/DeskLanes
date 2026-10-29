#Requires -Version 5.1
<#
  menu-groundtruth2.ps1 - ground truth via NATIVE desktop context menu (robust)

  Verifies what the OS itself changes when toggling "Show desktop icons":
    - registry diff (full Advanced key snapshot before/after)
    - DefView window visibility
    - SysListView32 UIA child count (icon count)
    - full screenshots
  All clicks are verified (WindowFromPoint) to land on desktop background; menu type is
  verified to be the desktop menu (not an icon menu) before proceeding.
#>
$ErrorActionPreference = 'Stop'

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$evidenceDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\stage5-evidence'))

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential)]
public struct Pt { public int X, Y; }

public static class DsWin7 {
    public static List<string> Top = new List<string>();
    public static List<string> Kids = new List<string>();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Pt Point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_LWIN = 0x5B;
    public const byte VK_ESC = 0x1B;
    public const byte VK_D = 0x44;
    public const uint GA_ROOT = 2;

    public static IntPtr WindowFromPointXY(int x, int y) { Pt p; p.X = x; p.Y = y; return WindowFromPoint(p); }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string Describe(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256);
        var tb = new StringBuilder(256); GetWindowText(h, tb, 256);
        uint p = 0; GetWindowThreadProcessId(h, out p);
        return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), p, IsWindowVisible(h));
    }
    public static string PointInfo(int x, int y) {
        var h = WindowFromPointXY(x, y);
        var root = GetAncestor(h, GA_ROOT);
        return string.Format("hwnd={0} rootClass={1}", Describe(h), ClassOf(root));
    }
}
'@

$topCb = { param($h, $l) [DsWin7]::Top.Add([DsWin7]::Describe($h)); return $true }
$kidCb = { param($h, $l) [DsWin7]::Kids.Add([DsWin7]::Describe($h)); return $true }

function Get-DesktopViews {
    [DsWin7]::Top.Clear()
    [void][DsWin7]::EnumWindows($topCb, [IntPtr]::Zero)
    $hosts = @()
    foreach ($line in @([DsWin7]::Top)) {
        $parts = $line.Split('|')
        if ($parts[1] -eq 'Progman' -or $parts[1] -eq 'WorkerW') { $hosts += ,$parts }
    }
    $result = @()
    foreach ($hp in $hosts) {
        $hostHwnd = [IntPtr]::new([Convert]::ToInt64($hp[0].Substring(2), 16))
        [DsWin7]::Kids.Clear()
        [void][DsWin7]::EnumChildWindows($hostHwnd, $kidCb, [IntPtr]::Zero)
        $kids = @([DsWin7]::Kids)
        foreach ($k in $kids) {
            $kp = $k.Split('|')
            if ($kp[1] -ne 'SHELLDLL_DefView') { continue }
            $dvHwnd = [IntPtr]::new([Convert]::ToInt64($kp[0].Substring(2), 16))
            $lvHwnd = [IntPtr]::Zero
            [DsWin7]::Kids.Clear()
            [void][DsWin7]::EnumChildWindows($dvHwnd, $kidCb, [IntPtr]::Zero)
            foreach ($g in @([DsWin7]::Kids)) {
                if ($g.Split('|')[1] -eq 'SysListView32') {
                    $lvHwnd = [IntPtr]::new([Convert]::ToInt64($g.Split('|')[0].Substring(2), 16))
                    break
                }
            }
            $result += [pscustomobject]@{
                Host = $hp[1]; DefViewHwnd = $dvHwnd; DefVisible = ($kp[4] -eq 'True'); ListViewHwnd = $lvHwnd
            }
        }
    }
    return $result
}

function Get-IconCountUia($lvHwnd) {
    if ($lvHwnd.ToInt64() -eq 0) { return -1 }
    try {
        $el = [System.Windows.Automation.AutomationElement]::FromHandle($lvHwnd)
        if ($null -eq $el) { return -1 }
        $kids = $el.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        return $kids.Count
    } catch { return -2 }
}

function Get-StateText {
    $views = Get-DesktopViews
    if ($views.Count -eq 0) { return 'no-defview' }
    $txt = @()
    foreach ($v in $views) {
        $iconCount = Get-IconCountUia $v.ListViewHwnd
        $txt += ("{0} def=0x{1:X} visible={2} icons(uia)={3}" -f $v.Host, $v.DefViewHwnd.ToInt64(), $v.DefVisible, $iconCount)
    }
    return ($txt -join ' | ')
}

function Get-RegSnapshot {
    $props = Get-ItemProperty -Path $advKey
    $lines = @()
    foreach ($pr in $props.PSObject.Properties) {
        if ($pr.Name -like 'PS*') { continue }
        $lines += ("{0}={1}" -f $pr.Name, $pr.Value)
    }
    return ($lines -join ';')
}

function Save-Shot($prefix) {
    $i = 0
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        if ($s.Bounds.Width -le 0 -or $s.Bounds.Height -le 0) { continue }
        $bmp = New-Object System.Drawing.Bitmap($s.Bounds.Width, $s.Bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CopyFromScreen($s.Bounds.Location, [System.Drawing.Point]::Empty, $s.Bounds.Size)
            $tag = $(if ($s.Primary) { 'primary' } else { "disp$i" })
            $bmp.Save((Join-Path $evidenceDir ("{0}-{1}.png" -f $prefix, $tag)), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $g.Dispose(); $bmp.Dispose() }
        $i++
    }
}

function Send-WinD {
    [DsWin7]::keybd_event([DsWin7]::VK_LWIN, 0, 0, 0)
    [DsWin7]::keybd_event([DsWin7]::VK_D, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [DsWin7]::keybd_event([DsWin7]::VK_D, 0, [DsWin7]::KEYEVENTF_KEYUP, 0)
    [DsWin7]::keybd_event([DsWin7]::VK_LWIN, 0, [DsWin7]::KEYEVENTF_KEYUP, 0)
}

function Send-Esc {
    [DsWin7]::keybd_event([DsWin7]::VK_ESC, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [DsWin7]::keybd_event([DsWin7]::VK_ESC, 0, [DsWin7]::KEYEVENTF_KEYUP, 0)
}

function Right-Click-At($x, $y) {
    [void][DsWin7]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [DsWin7]::mouse_event([DsWin7]::MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [DsWin7]::mouse_event([DsWin7]::MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0)
}

function Get-MenuWindows {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32768')
    return $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Wait-Menu([int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $menus = Get-MenuWindows
        if ($menus.Count -gt 0) { return $menus }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Get-MenuItems($menuEl) {
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $items = $menuEl.FindAll([System.Windows.Automation.TreeScope]::Subtree, $cond)
    $rows = @()
    foreach ($it in $items) {
        $toggle = 'n/a'
        try {
            $tp = $it.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            $toggle = $tp.Current.ToggleState.ToString()
        } catch { }
        $rows += [pscustomobject]@{ Name = $it.Current.Name; Enabled = $it.Current.IsEnabled; Toggle = $toggle; Element = $it }
    }
    return $rows
}

function Invoke-Element2($el) {
    $ip = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $ip.Invoke()
}

function Get-DesktopPoints {
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $pts = @()
    $pts += @{ X = [int]($wa.Right - 150); Y = [int]($wa.Top + $wa.Height * 0.3) }
    $pts += @{ X = [int]($wa.Right - 150); Y = [int]($wa.Top + $wa.Height * 0.6) }
    $pts += @{ X = [int]($wa.Right - 300); Y = [int]($wa.Top + $wa.Height * 0.45) }
    $pts += @{ X = [int]($wa.Left + $wa.Width * 0.55); Y = [int]($wa.Top + $wa.Height * 0.5) }
    $pts += @{ X = [int]($wa.Right - 80); Y = [int]($wa.Bottom - 120) }
    $desktopPts = @()
    foreach ($p in $pts) {
        $info = [DsWin7]::PointInfo($p.X, $p.Y)
        if ($info -match 'rootClass=Progman') {
            $desktopPts += @{ X = $p.X; Y = $p.Y; Info = $info }
        }
    }
    return $desktopPts
}

function Open-DesktopMenuAndGetItems {
    $pts = Get-DesktopPoints
    if ($pts.Count -eq 0) {
        Send-WinD
        Start-Sleep -Milliseconds 1000
        $pts = Get-DesktopPoints
    }
    foreach ($p in $pts) {
        Right-Click-At $p.X $p.Y
        $menus = Wait-Menu 2500
        if ($null -eq $menus) { continue }
        $items = @()
        foreach ($m in $menus) { $items += Get-MenuItems $m }
        $hasView = $false
        foreach ($it in $items) { if ($it.Name -like '*查看*' -or $it.Name -like '*View*') { $hasView = $true } }
        if (-not $hasView) {
            Write-Output ("    (click at {0},{1} hit an icon menu - skipping)" -f $p.X, $p.Y)
            Send-Esc
            continue
        }
        return $items
    }
    return $null
}

$origReg = Get-RegSnapshot
Write-Output "===== menu ground truth v3 start ====="
Write-Output ("[before] state: {0}" -f (Get-StateText))
Write-Output ("[before] registry: {0}" -f $origReg)
Save-Shot 'gt3-01-before'

$script:menuWasOpen = $false
try {
    Write-Output "--- step 1: open desktop context menu ---"
    $items = Open-DesktopMenuAndGetItems
    if ($null -eq $items) { throw "could not open desktop context menu" }
    Write-Output "--- top-level menu items ---"
    foreach ($it in $items) { Write-Output ("    name='{0}' enabled={1} toggle={2}" -f $it.Name, $it.Enabled, $it.Toggle) }

    $viewItem = $null
    foreach ($it in $items) { if ($it.Name -like '*查看*' -or $it.Name -like '*View*') { $viewItem = $it; break } }
    if ($null -eq $viewItem) { throw "View item not found" }
    Invoke-Element2 $viewItem.Element
    Start-Sleep -Milliseconds 700

    $menus2 = Wait-Menu 2000
    if ($null -eq $menus2) { throw "submenu did not appear" }
    $items2 = @()
    foreach ($m in $menus2) { $items2 += Get-MenuItems $m }
    Write-Output "--- items after expanding View ---"
    foreach ($it in $items2) { Write-Output ("    name='{0}' enabled={1} toggle={2}" -f $it.Name, $it.Enabled, $it.Toggle) }

    $showItem = $null
    foreach ($it in $items2) { if ($it.Name -like '*显示桌面图标*' -or $it.Name -like '*Show desktop*') { $showItem = $it; break } }
    if ($null -eq $showItem) { throw "'Show desktop icons' item not found" }
    Write-Output ("--- toggle state BEFORE invoke: {0} ---" -f $showItem.Toggle)

    Write-Output "--- step 2: invoke 'Show desktop icons' (expect HIDE) ---"
    Invoke-Element2 $showItem.Element
    Start-Sleep -Milliseconds 2000
    Send-Esc
    Start-Sleep -Milliseconds 500
    $regAfterHide = Get-RegSnapshot
    Write-Output ("[after-hide] state: {0}" -f (Get-StateText))
    Save-Shot 'gt3-02-after-hide'

    Write-Output "--- step 3: reopen menu and toggle back (expect SHOW) ---"
    $items3 = Open-DesktopMenuAndGetItems
    if ($null -eq $items3) { throw "could not reopen desktop context menu" }
    $viewItem3 = $null
    foreach ($it in $items3) { if ($it.Name -like '*查看*' -or $it.Name -like '*View*') { $viewItem3 = $it; break } }
    if ($null -eq $viewItem3) { throw "View item not found on reopen" }
    Invoke-Element2 $viewItem3.Element
    Start-Sleep -Milliseconds 700
    $menus4 = Wait-Menu 2000
    if ($null -eq $menus4) { throw "submenu did not reappear" }
    $items4 = @()
    foreach ($m in $menus4) { $items4 += Get-MenuItems $m }
    $showItem4 = $null
    foreach ($it in $items4) { if ($it.Name -like '*显示桌面图标*' -or $it.Name -like '*Show desktop*') { $showItem4 = $it; break } }
    if ($null -eq $showItem4) { throw "'Show desktop icons' not found on reopen" }
    Write-Output ("--- toggle state BEFORE 2nd invoke: {0} ---" -f $showItem4.Toggle)
    Invoke-Element2 $showItem4.Element
    Start-Sleep -Milliseconds 2000
    Send-Esc
    Start-Sleep -Milliseconds 500
    $regAfterShow = Get-RegSnapshot
    Write-Output ("[after-show] state: {0}" -f (Get-StateText))
    Save-Shot 'gt3-03-after-show'

    Write-Output "===== registry diff: before -> after-hide ====="
    $beforePairs = @{}
    foreach ($pair in $origReg.Split(';')) { if ($pair -ne '') { $kv = $pair.Split('=', 2); $beforePairs[$kv[0]] = $kv[1] } }
    $hidePairs = @{}
    foreach ($pair in $regAfterHide.Split(';')) { if ($pair -ne '') { $kv = $pair.Split('=', 2); $hidePairs[$kv[0]] = $kv[1] } }
    $allKeys = @()
    foreach ($k in $beforePairs.Keys) { if ($allKeys -notcontains $k) { $allKeys += $k } }
    foreach ($k in $hidePairs.Keys) { if ($allKeys -notcontains $k) { $allKeys += $k } }
    foreach ($k in $allKeys) {
        $b = $(if ($beforePairs.ContainsKey($k)) { $beforePairs[$k] } else { '<absent>' })
        $h = $(if ($hidePairs.ContainsKey($k)) { $hidePairs[$k] } else { '<absent>' })
        if ($b -ne $h) { Write-Output ("    CHANGED: {0}: '{1}' -> '{2}'" -f $k, $b, $h) }
    }
    Write-Output "===== registry diff: before -> after-show ====="
    $showPairs = @{}
    foreach ($pair in $regAfterShow.Split(';')) { if ($pair -ne '') { $kv = $pair.Split('=', 2); $showPairs[$kv[0]] = $kv[1] } }
    foreach ($k in $allKeys) {
        $b = $(if ($beforePairs.ContainsKey($k)) { $beforePairs[$k] } else { '<absent>' })
        $s = $(if ($showPairs.ContainsKey($k)) { $showPairs[$k] } else { '<absent>' })
        if ($b -ne $s) { Write-Output ("    CHANGED: {0}: '{1}' -> '{2}'" -f $k, $b, $s) }
    }
}
finally {
    Write-Output "===== finally: close menus, restore windows ====="
    Send-Esc
    Start-Sleep -Milliseconds 300
    Send-Esc
    Send-WinD
    Start-Sleep -Milliseconds 800
    Write-Output ("[final] state: {0}" -f (Get-StateText))
    Write-Output ("[final] registry HideIcons: {0}" -f (Get-RegSnapshot).Split(';')[0])
    Save-Shot 'gt3-04-final'
}
Write-Output "===== menu ground truth v3 end ====="
