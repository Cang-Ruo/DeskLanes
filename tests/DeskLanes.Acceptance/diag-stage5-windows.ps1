#Requires -Version 5.1
<#
  diag-stage5-windows.ps1 - dump desktop window hierarchy + current state + screenshot
#>
$ErrorActionPreference = 'Stop'
$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$evidenceDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\stage5-evidence'))
if (-not (Test-Path -LiteralPath $evidenceDir)) { New-Item -ItemType Directory -Path $evidenceDir -Force | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DsWin2 {
    public static List<string> Log = new List<string>();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr FindWindow(string c, string w);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
}
'@

$interesting = @('Progman','WorkerW','SHELLDLL_DefView','SysListView32','CabinetWET','ExploreWClass','#32770','Windows.UI.Core.CoreWindow','Internet Explorer_Server')

$cb = {
    param($h, $l)
    $sb = New-Object System.Text.StringBuilder 256
    [void][DsWin2]::GetClassName($h, $sb, 256)
    $cls = $sb.ToString()
    if ($interesting -contains $cls) {
        $tb = New-Object System.Text.StringBuilder 256
        [void][DsWin2]::GetWindowText($h, $tb, 256)
        $pidv = 0
        [void][DsWin2]::GetWindowThreadProcessId($h, [ref]$pidv)
        $vis = [DsWin2]::IsWindowVisible($h)
        [DsWin2]::Log.Add(("hwnd=0x{0:X} class={1} title='{2}' pid={3} visible={4}" -f $h.ToInt64(), $cls, $tb.ToString(), $pidv, $vis))
    }
    return $true
}

Write-Output ("HideIcons now = {0}" -f $(if ($null -eq (Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue)) { 'ABSENT' } else { (Get-ItemProperty -Path $advKey -Name HideIcons).HideIcons }))
Write-Output ("monitors (Screen.AllScreens) = {0}" -f [System.Windows.Forms.Screen]::AllScreens.Count)

[void][DsWin2]::EnumWindows($cb, [IntPtr]::Zero)
Write-Output "--- top-level interesting windows ---"
foreach ($line in [DsWin2]::Log) { Write-Output ("  " + $line) }

Write-Output "--- Progman children ---"
$pm = [DsWin2]::FindWindow('Progman', $null)
Write-Output ("Progman = 0x{0:X}" -f $pm.ToInt64())
[DsWin2]::Log.Clear()
[void][DsWin2]::EnumChildWindows($pm, $cb, [IntPtr]::Zero)
foreach ($line in [DsWin2]::Log) { Write-Output ("  " + $line) }

Write-Output "--- WorkerW children (first 3 WorkerW) ---"
[DsWin2]::Log.Clear()
$w = [DsWin2]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', $null)
$n = 0
while ($w.ToInt64() -ne 0 -and $n -lt 3) {
    Write-Output ("WorkerW 0x{0:X} children:" -f $w.ToInt64())
    [DsWin2]::Log.Clear()
    [void][DsWin2]::EnumChildWindows($w, $cb, [IntPtr]::Zero)
    foreach ($line in [DsWin2]::Log) { Write-Output ("  " + $line) }
    $w = [DsWin2]::FindWindowEx([IntPtr]::Zero, $w, 'WorkerW', $null)
    $n++
}
if ($n -eq 0) { Write-Output "  (no WorkerW found)" }

# screenshot for visual confirmation
$i = 0
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
    if ($s.Bounds.Width -le 0 -or $s.Bounds.Height -le 0) { continue }
    $bmp = New-Object System.Drawing.Bitmap($s.Bounds.Width, $s.Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($s.Bounds.Location, [System.Drawing.Point]::Empty, $s.Bounds.Size)
        $tag = $(if ($s.Primary) { 'primary' } else { "disp$i" })
        $path = Join-Path $evidenceDir ("diag-current-{0}.png" -f $tag)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output ("screenshot: {0}" -f $path)
    } finally {
        $g.Dispose(); $bmp.Dispose()
    }
    $i++
}
