#Requires -Version 5.1
<#
  menu-groundtruth4.ps1 - ground truth via NATIVE menu driven by KEYBOARD ACCELERATORS
  (Win11 desktop context menu is NOT class #32768, so UIA window search fails; but the menu
   responds to accelerator keys: V = View submenu, D = Show desktop icons)

  Records: full registry snapshot before/after each toggle, DefView visibility, screenshots.
#>
$ErrorActionPreference = 'Stop'
$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
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

public static class DsWin9 {
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
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Pt Point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_ESC = 0x1B;
    public const byte VK_V = 0x56;
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
    public static void TapKey(byte vk) {
        keybd_event(vk, 0, 0, 0);
        System.Threading.Thread.Sleep(60);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);
    }
}
'@

$topCb = { param($h, $l) [DsWin9]::Top.Add([DsWin9]::Describe($h)); return $true }
$kidCb = { param($h, $l) [DsWin9]::Kids.Add([DsWin9]::Describe($h)); return $true }

function Get-DesktopViews {
    [DsWin9]::Top.Clear()
    [void][DsWin9]::EnumWindows($topCb, [IntPtr]::Zero)
    $hosts = @()
    foreach ($line in @([DsWin9]::Top)) {
        $parts = $line.Split('|')
        if ($parts[1] -eq 'Progman' -or $parts[1] -eq 'WorkerW') { $hosts += ,$parts }
    }
    $result = @()
    foreach ($hp in $hosts) {
        $hostHwnd = [IntPtr]::new([Convert]::ToInt64($hp[0].Substring(2), 16))
        [DsWin9]::Kids.Clear()
        [void][DsWin9]::EnumChildWindows($hostHwnd, $kidCb, [IntPtr]::Zero)
        foreach ($k in @([DsWin9]::Kids)) {
            $kp = $k.Split('|')
            if ($kp[1] -ne 'SHELLDLL_DefView') { continue }
            $dvHwnd = [IntPtr]::new([Convert]::ToInt64($kp[0].Substring(2), 16))
            $result += [pscustomobject]@{ Host = $hp[1]; DefViewHwnd = $dvHwnd; DefVisible = ($kp[4] -eq 'True') }
        }
    }
    return $result
}

function Get-StateText {
    $views = Get-DesktopViews
    if ($views.Count -eq 0) { return 'no-defview' }
    $txt = @()
    foreach ($v in $views) { $txt += ("{0} def=0x{1:X} visible={2}" -f $v.Host, $v.DefViewHwnd.ToInt64(), $v.DefVisible) }
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

function Right-Click-Desktop {
    # (0,0) verified to hit desktop background (opens desktop menu, not icon menu)
    [void][DsWin9]::SetCursorPos(0, 0)
    Start-Sleep -Milliseconds 150
    [DsWin9]::mouse_event([DsWin9]::MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 80
    [DsWin9]::mouse_event([DsWin9]::MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 600
}

function Toggle-ShowDesktopIcons {
    Right-Click-Desktop
    [DsWin9]::TapKey([DsWin9]::VK_V)   # View submenu
    Start-Sleep -Milliseconds 500
    [DsWin9]::TapKey([DsWin9]::VK_D)   # Show desktop icons
    Start-Sleep -Milliseconds 2200
    [DsWin9]::TapKey([DsWin9]::VK_ESC)
    Start-Sleep -Milliseconds 400
}

$origReg = Get-RegSnapshot
Write-Output "===== menu ground truth v4 (keyboard-driven) ====="
Write-Output ("[before] state: {0}" -f (Get-StateText))
Write-Output ("[before] HideIcons: {0}" -f ((Get-RegSnapshot).Split(';') | Where-Object { $_ -like 'HideIcons=*' }))
Save-Shot 'gt4-01-before'

try {
    Write-Output "--- toggle #1 (expect HIDE) ---"
    Toggle-ShowDesktopIcons
    $reg1 = Get-RegSnapshot
    Write-Output ("[after-toggle1] state: {0}" -f (Get-StateText))
    Write-Output ("[after-toggle1] HideIcons: {0}" -f ($reg1.Split(';') | Where-Object { $_ -like 'HideIcons=*' }))
    Save-Shot 'gt4-02-after-toggle1'

    Write-Output "--- toggle #2 (expect SHOW) ---"
    Toggle-ShowDesktopIcons
    $reg2 = Get-RegSnapshot
    Write-Output ("[after-toggle2] state: {0}" -f (Get-StateText))
    Write-Output ("[after-toggle2] HideIcons: {0}" -f ($reg2.Split(';') | Where-Object { $_ -like 'HideIcons=*' }))
    Save-Shot 'gt4-03-after-toggle2'

    Write-Output "===== registry diff: before -> after-toggle1 ====="
    $beforePairs = @{}
    foreach ($pair in $origReg.Split(';')) { if ($pair -ne '') { $kv = $pair.Split('=', 2); $beforePairs[$kv[0]] = $kv[1] } }
    $p1 = @{}
    foreach ($pair in $reg1.Split(';')) { if ($pair -ne '') { $kv = $pair.Split('=', 2); $p1[$kv[0]] = $kv[1] } }
    $allKeys = @()
    foreach ($k in $beforePairs.Keys) { if ($allKeys -notcontains $k) { $allKeys += $k } }
    foreach ($k in $p1.Keys) { if ($allKeys -notcontains $k) { $allKeys += $k } }
    foreach ($k in $allKeys) {
        $b = $(if ($beforePairs.ContainsKey($k)) { $beforePairs[$k] } else { '<absent>' })
        $h = $(if ($p1.ContainsKey($k)) { $p1[$k] } else { '<absent>' })
        if ($b -ne $h) { Write-Output ("    CHANGED: {0}: '{1}' -> '{2}'" -f $k, $b, $h) }
    }
    Write-Output "===== registry diff: before -> after-toggle2 ====="
    $p2 = @{}
    foreach ($pair in $reg2.Split(';')) { if ($pair -ne '') { $kv = $pair.Split('=', 2); $p2[$kv[0]] = $kv[1] } }
    foreach ($k in $allKeys) {
        $b = $(if ($beforePairs.ContainsKey($k)) { $beforePairs[$k] } else { '<absent>' })
        $s = $(if ($p2.ContainsKey($k)) { $p2[$k] } else { '<absent>' })
        if ($b -ne $s) { Write-Output ("    CHANGED: {0}: '{1}' -> '{2}'" -f $k, $b, $s) }
    }
}
finally {
    Write-Output "===== finally: ensure shown state ====="
    $views = Get-DesktopViews
    $allVisible = $false
    if ($views.Count -gt 0) { $allVisible = @($views | Where-Object { $_.DefVisible }).Count -eq $views.Count }
    if (-not $allVisible) {
        Write-Output "    icons hidden - toggling back via menu"
        Toggle-ShowDesktopIcons
    }
    [DsWin9]::TapKey([DsWin9]::VK_ESC)
    Write-Output ("[final] state: {0}" -f (Get-StateText))
    Write-Output ("[final] HideIcons: {0}" -f ((Get-RegSnapshot).Split(';') | Where-Object { $_ -like 'HideIcons=*' }))
    Save-Shot 'gt4-04-final'
}
Write-Output "===== menu ground truth v4 end ====="
