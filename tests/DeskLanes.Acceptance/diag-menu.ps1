#Requires -Version 5.1
<#
  diag-menu.ps1 - diagnose why the desktop context menu did not open
#>
$ErrorActionPreference = 'Stop'
$evidenceDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\stage5-evidence'))

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential)]
public struct Pt { public int X, Y; }

public static class DsWin6 {
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
    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;

    public static IntPtr WindowFromPointXY(int x, int y) { Pt p; p.X = x; p.Y = y; return WindowFromPoint(p); }
    public static string Describe(IntPtr h) {
        var sb = new StringBuilder(256); GetClassName(h, sb, 256);
        var tb = new StringBuilder(256); GetWindowText(h, tb, 256);
        uint p = 0; GetWindowThreadProcessId(h, out p);
        return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), p, IsWindowVisible(h));
    }
    public static string DescribePoint(int x, int y) {
        var h = WindowFromPointXY(x, y);
        var root = GetAncestor(h, GA_ROOT);
        return string.Format("hwnd={0} root=0x{1:X} class={2}", Describe(h), root.ToInt64(), ClassOf(root));
    }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
}
'@

$topCb = { param($h, $l) [DsWin6]::Top.Add([DsWin6]::Describe($h)); return $true }

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
    [DsWin6]::keybd_event([DsWin6]::VK_LWIN, 0, 0, 0)
    [DsWin6]::keybd_event([DsWin6]::VK_D, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [DsWin6]::keybd_event([DsWin6]::VK_D, 0, [DsWin6]::KEYEVENTF_KEYUP, 0)
    [DsWin6]::keybd_event([DsWin6]::VK_LWIN, 0, [DsWin6]::KEYEVENTF_KEYUP, 0)
}
function Send-Esc {
    [DsWin6]::keybd_event([DsWin6]::VK_ESC, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [DsWin6]::keybd_event([DsWin6]::VK_ESC, 0, [DsWin6]::KEYEVENTF_KEYUP, 0)
}

Save-Shot 'mg-diag-01-before'
Write-Output "===== before Win+D ====="
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
Write-Output ("workarea: {0}" -f $wa)
Write-Output ("center point: {0}" -f ([DsWin6]::DescribePoint([int]($wa.Left + $wa.Width / 2), [int]($wa.Top + $wa.Height / 2))))

Write-Output "===== sending Win+D ====="
Send-WinD
Start-Sleep -Milliseconds 1200
Save-Shot 'mg-diag-02-after-wind'
[DsWin6]::Top.Clear()
[void][DsWin6]::EnumWindows($topCb, [IntPtr]::Zero)
Write-Output "--- visible top-level windows after Win+D ---"
foreach ($line in [DsWin6]::Top) {
    $parts = $line.Split('|')
    if ($parts[4] -eq 'True') { Write-Output ("  " + $line) }
}

Write-Output "===== probing candidate click points (WindowFromPoint) ====="
$candidates = @()
$candidates += ("center", [int]($wa.Left + $wa.Width / 2), [int]($wa.Top + $wa.Height / 2))
$candidates += ("center-right", [int]($wa.Left + $wa.Width * 0.75), [int]($wa.Top + $wa.Height / 2))
$candidates += ("bottom-center", [int]($wa.Left + $wa.Width / 2), [int]($wa.Bottom - 80))
$candidates += ("bottom-right", [int]($wa.Right - 60), [int]($wa.Bottom - 60))
foreach ($c in $candidates) {
    Write-Output ("  {0}: {1}" -f $c[0], ([DsWin6]::DescribePoint($c[1], $c[2])))
}

Write-Output "===== right-click at bottom-center and check for #32768 ====="
$bx = [int]($wa.Left + $wa.Width / 2); $by = [int]($wa.Bottom - 80)
[void][DsWin6]::SetCursorPos($bx, $by)
Start-Sleep -Milliseconds 120
[DsWin6]::mouse_event([DsWin6]::MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0)
Start-Sleep -Milliseconds 60
[DsWin6]::mouse_event([DsWin6]::MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0)
Start-Sleep -Milliseconds 800
Save-Shot 'mg-diag-03-after-rightclick'
[DsWin6]::Top.Clear()
[void][DsWin6]::EnumWindows($topCb, [IntPtr]::Zero)
$menuCount = 0
foreach ($line in [DsWin6]::Top) {
    $parts = $line.Split('|')
    if ($parts[1] -eq '#32768') { Write-Output ("  MENU: " + $line); $menuCount++ }
}
Write-Output ("menu windows found: {0}" -f $menuCount)
Send-Esc
Start-Sleep -Milliseconds 300
Send-Esc
Write-Output "===== sending Win+D again (restore windows) ====="
Send-WinD
Start-Sleep -Milliseconds 800
Save-Shot 'mg-diag-04-restored'
