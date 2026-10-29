#Requires -Version 5.1
<#
  diag-menu2.ps1 - step-by-step diagnosis of desktop context menu opening
#>
$ErrorActionPreference = 'Continue'
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

public static class DsWin8 {
    public static List<string> Top = new List<string>();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
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
    public const byte VK_APPS = 0x5D;
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

$topCb = { param($h, $l) [DsWin8]::Top.Add([DsWin8]::Describe($h)); return $true }

function Dump-VisibleWindows($label) {
    [DsWin8]::Top.Clear()
    [void][DsWin8]::EnumWindows($topCb, [IntPtr]::Zero)
    Write-Output ("[{0}] visible top-level windows:" -f $label)
    foreach ($line in [DsWin8]::Top) {
        $parts = $line.Split('|')
        if ($parts[4] -eq 'True') { Write-Output ("    " + $line) }
    }
}

function Find-DesktopPoints {
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $pts = @()
    $pts += @{ X = [int]($wa.Right - 150); Y = [int]($wa.Top + $wa.Height * 0.3) }
    $pts += @{ X = [int]($wa.Right - 150); Y = [int]($wa.Top + $wa.Height * 0.6) }
    $pts += @{ X = [int]($wa.Left + $wa.Width * 0.55); Y = [int]($wa.Top + $wa.Height * 0.5) }
    $pts += @{ X = [int]($wa.Right - 80); Y = [int]($wa.Bottom - 120) }
    $result = @()
    foreach ($p in $pts) {
        $info = [DsWin8]::PointInfo($p.X, $p.Y)
        $isDesktop = $info -match 'rootClass=Progman'
        Write-Output ("    point {0},{1}: {2} => desktop={3}" -f $p.X, $p.Y, $info, $isDesktop)
        if ($isDesktop) { $result += $p }
    }
    return $result
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
    [DsWin8]::keybd_event([DsWin8]::VK_LWIN, 0, 0, 0)
    [DsWin8]::keybd_event([DsWin8]::VK_D, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [DsWin8]::keybd_event([DsWin8]::VK_D, 0, [DsWin8]::KEYEVENTF_KEYUP, 0)
    [DsWin8]::keybd_event([DsWin8]::VK_LWIN, 0, [DsWin8]::KEYEVENTF_KEYUP, 0)
}

function Send-Esc {
    [DsWin8]::keybd_event([DsWin8]::VK_ESC, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [DsWin8]::keybd_event([DsWin8]::VK_ESC, 0, [DsWin8]::KEYEVENTF_KEYUP, 0)
}

Dump-VisibleWindows 'before'
Write-Output "===== probing desktop points BEFORE Win+D ====="
$pts = Find-DesktopPoints
Write-Output ("desktop points found: {0}" -f $pts.Count)

Write-Output "===== sending Win+D ====="
Send-WinD
Start-Sleep -Milliseconds 1500
Dump-VisibleWindows 'after-wind'
Write-Output "===== probing desktop points AFTER Win+D ====="
$pts2 = Find-DesktopPoints
Write-Output ("desktop points found after Win+D: {0}" -f $pts2.Count)
Save-Shot 'mg2-after-wind'

if ($pts2.Count -gt 0) {
    $p = $pts2[0]
    Write-Output ("===== right-click at {0},{1} =====" -f $p.X, $p.Y)
    [void][DsWin8]::SetCursorPos($p.X, $p.Y)
    Start-Sleep -Milliseconds 150
    [DsWin8]::mouse_event([DsWin8]::MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [DsWin8]::mouse_event([DsWin8]::MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 1000
    Save-Shot 'mg2-after-rightclick'

    [DsWin8]::Top.Clear()
    [void][DsWin8]::EnumWindows($topCb, [IntPtr]::Zero)
    $menuCount = 0
    foreach ($line in [DsWin8]::Top) {
        $parts = $line.Split('|')
        if ($parts[1] -eq '#32768') { Write-Output ("  EnumWindows MENU: " + $line); $menuCount++ }
    }
    Write-Output ("menu windows (EnumWindows): {0}" -f $menuCount)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32768')
    $uiaMenus = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    Write-Output ("menu windows (UIA): {0}" -f $uiaMenus.Count)
    foreach ($m in $uiaMenus) {
        $itemCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
        $items = $m.FindAll([System.Windows.Automation.TreeScope]::Subtree, $itemCond)
        Write-Output ("  menu items ({0}):" -f $items.Count)
        foreach ($it in $items) { Write-Output ("    name='{0}'" -f $it.Current.Name) }
    }
    Send-Esc
    Start-Sleep -Milliseconds 400
}

Write-Output "===== sending Win+D again (restore windows) ====="
Send-WinD
Start-Sleep -Milliseconds 1000
Save-Shot 'mg2-restored'
