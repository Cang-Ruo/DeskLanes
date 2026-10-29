#Requires -Version 5.1
<#
  menu-groundtruth.ps1 - ground-truth experiment: toggle "Show desktop icons" via the NATIVE
  desktop context menu and observe what actually changes (registry values / window state).

  Purpose: registry-write alone did NOT hide icons on this build. The native menu MUST work.
  Diffing before/after reveals the real mechanism we need to replicate.
#>
$ErrorActionPreference = 'Stop'

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$evidenceDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\stage5-evidence'))
if (-not (Test-Path -LiteralPath $evidenceDir)) { New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DsWin5 {
    public static List<string> Top = new List<string>();
    public static List<string> Kids = new List<string>();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_LWIN = 0x5B;
    public const byte VK_ESC = 0x1B;
    public const byte VK_D = 0x44;

    public static string Describe(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256);
        var tb = new StringBuilder(256); GetWindowText(h, tb, 256);
        uint p = 0; GetWindowThreadProcessId(h, out p);
        return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), p, IsWindowVisible(h));
    }
}
'@

$topCb = { param($h, $l) [DsWin5]::Top.Add([DsWin5]::Describe($h)); return $true }
$kidCb = { param($h, $l) [DsWin5]::Kids.Add([DsWin5]::Describe($h)); return $true }

function Get-DesktopViewState {
    [DsWin5]::Top.Clear()
    [void][DsWin5]::EnumWindows($topCb, [IntPtr]::Zero)
    $result = @()
    foreach ($line in [DsWin5]::Top) {
        $parts = $line.Split('|')
        if ($parts[1] -ne 'Progman' -and $parts[1] -ne 'WorkerW') { continue }
        $hostHwnd = [IntPtr]::new([Convert]::ToInt64($parts[0].Substring(2), 16))
        [DsWin5]::Kids.Clear()
        [void][DsWin5]::EnumChildWindows($hostHwnd, $kidCb, [IntPtr]::Zero)
        foreach ($k in [DsWin5]::Kids) {
            $kp = $k.Split('|')
            if ($kp[1] -ne 'SHELLDLL_DefView') { continue }
            $dvHwnd = [IntPtr]::new([Convert]::ToInt64($kp[0].Substring(2), 16))
            $result += [pscustomobject]@{ Host = $parts[1]; DefView = $kp[0]; Visible = ($kp[4] -eq 'True') }
        }
    }
    return $result
}

function Get-ViewStateText {
    $views = Get-DesktopViewState
    if ($views.Count -eq 0) { return 'no-defview' }
    $txt = @()
    foreach ($v in $views) { $txt += ("{0}:{1}:{2}" -f $v.Host, $v.DefView, $(if ($v.Visible) { 'visible' } else { 'hidden' })) }
    return ($txt -join ', ')
}

function Get-RegSnapshot {
    $props = Get-ItemProperty -Path $advKey
    $lines = @()
    foreach ($pr in $props.PSObject.Properties) {
        if ($pr.Name -like 'PS*') { continue }
        $lines += ("{0}={1}" -f $pr.Name, $pr.Value)
    }
    return ($lines -join '; ')
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
    [DsWin5]::keybd_event([DsWin5]::VK_LWIN, 0, 0, 0)
    [DsWin5]::keybd_event([DsWin5]::VK_D, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [DsWin5]::keybd_event([DsWin5]::VK_D, 0, [DsWin5]::KEYEVENTF_KEYUP, 0)
    [DsWin5]::keybd_event([DsWin5]::VK_LWIN, 0, [DsWin5]::KEYEVENTF_KEYUP, 0)
}

function Send-Esc {
    [DsWin5]::keybd_event([DsWin5]::VK_ESC, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [DsWin5]::keybd_event([DsWin5]::VK_ESC, 0, [DsWin5]::KEYEVENTF_KEYUP, 0)
}

function Right-Click-At($x, $y) {
    [void][DsWin5]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 100
    [DsWin5]::mouse_event([DsWin5]::MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [DsWin5]::mouse_event([DsWin5]::MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0)
}

function Get-MenuWindows {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32768')
    $found = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    return $found
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

$origReg = Get-RegSnapshot
Write-Output "===== menu ground truth start ====="
Write-Output ("[before] view state: {0}" -f (Get-ViewStateText))
Write-Output ("[before] registry: {0}" -f $origReg)
Save-Shot 'gt-01-before'

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$clickX = [int]($wa.Left + ($wa.Width / 2))
$clickY = [int]($wa.Top + ($wa.Height / 2))

try {
    Write-Output "--- open desktop context menu (Win+D + right-click at desktop center) ---"
    Send-WinD
    Start-Sleep -Milliseconds 800
    Right-Click-At $clickX $clickY
    $menus = Wait-Menu 3000
    if ($null -eq $menus) { throw "no context menu appeared" }
    Write-Output ("menu windows: {0}" -f $menus.Count)
    $items = @()
    foreach ($m in $menus) { $items += Get-MenuItems $m }
    Write-Output "--- top-level menu items ---"
    foreach ($it in $items) { Write-Output ("    name='{0}' enabled={1} toggle={2}" -f $it.Name, $it.Enabled, $it.Toggle) }

    $viewItem = $null
    foreach ($it in $items) { if ($it.Name -like '*查看*' -or $it.Name -like '*View*') { $viewItem = $it; break } }
    if ($null -eq $viewItem) { throw "View submenu item not found (see dump above)" }
    Write-Output ("--- invoking View item: '{0}' ---" -f $viewItem.Name)
    Invoke-Element2 $viewItem.Element
    Start-Sleep -Milliseconds 700

    $menus2 = Wait-Menu 2000
    if ($null -eq $menus2) { throw "submenu did not appear" }
    $items2 = @()
    foreach ($m in $menus2) { $items2 += Get-MenuItems $m }
    Write-Output "--- menu items after expanding View ---"
    foreach ($it in $items2) { Write-Output ("    name='{0}' enabled={1} toggle={2}" -f $it.Name, $it.Enabled, $it.Toggle) }

    $showItem = $null
    foreach ($it in $items2) { if ($it.Name -like '*显示桌面图标*' -or $it.Name -like '*Show desktop*') { $showItem = $it; break } }
    if ($null -eq $showItem) { throw "'Show desktop icons' item not found (see dump above)" }
    Write-Output ("--- 'Show desktop icons' toggle state BEFORE: {0} ---" -f $showItem.Toggle)

    Write-Output "--- invoking 'Show desktop icons' (expect HIDE) ---"
    Invoke-Element2 $showItem.Element
    Start-Sleep -Milliseconds 1800
    Send-Esc
    Start-Sleep -Milliseconds 400

    $regAfterHide = Get-RegSnapshot
    Write-Output ("[after-hide] view state: {0}" -f (Get-ViewStateText))
    Write-Output ("[after-hide] registry: {0}" -f $regAfterHide)
    Save-Shot 'gt-02-after-hide'

    Write-Output "--- reopen menu and toggle back (expect SHOW) ---"
    Right-Click-At $clickX $clickY
    $menus3 = Wait-Menu 3000
    if ($null -eq $menus3) { throw "context menu did not reappear" }
    $items3 = @()
    foreach ($m in $menus3) { $items3 += Get-MenuItems $m }
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
    Write-Output ("--- 'Show desktop icons' toggle state BEFORE 2nd invoke: {0} ---" -f $showItem4.Toggle)
    Invoke-Element2 $showItem4.Element
    Start-Sleep -Milliseconds 1800
    Send-Esc
    Start-Sleep -Milliseconds 400

    $regAfterShow = Get-RegSnapshot
    Write-Output ("[after-show] view state: {0}" -f (Get-ViewStateText))
    Write-Output ("[after-show] registry: {0}" -f $regAfterShow)
    Save-Shot 'gt-03-after-show'

    Write-Output "===== registry diff (before -> after-hide) ====="
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
    Write-Output "===== registry diff (before -> after-show) ====="
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
    Start-Sleep -Milliseconds 600
    Write-Output ("[final] view state: {0}" -f (Get-ViewStateText))
    Save-Shot 'gt-04-final'
}
Write-Output "===== menu ground truth end ====="
