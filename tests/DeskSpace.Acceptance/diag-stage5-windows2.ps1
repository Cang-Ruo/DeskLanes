#Requires -Version 5.1
<#
  diag-stage5-windows2.ps1 - proper desktop hierarchy dump + FindWindow behavior test
  Root cause hunt: FindWindow('Progman', $null) returns 0 while EnumWindows sees Progman.
#>
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DsWin3 {
    public static List<string> Top = new List<string>();
    public static List<string> Kids = new List<string>();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr FindWindow(string c, string w);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
    [DllImport("kernel32.dll")] public static extern uint GetLastError();
}
'@

$topCb = {
    param($h, $l)
    $sb = New-Object System.Text.StringBuilder 256
    [void][DsWin3]::GetClassName($h, $sb, 256)
    $tb = New-Object System.Text.StringBuilder 256
    [void][DsWin3]::GetWindowText($h, $tb, 256)
    $pidv = 0
    [void][DsWin3]::GetWindowThreadProcessId($h, [ref]$pidv)
    [DsWin3]::Top.Add(("0x{0:X}|{1}|{2}|{3}|{4}" -f $h.ToInt64(), $sb.ToString(), $tb.ToString(), $pidv, [DsWin3]::IsWindowVisible($h)))
    return $true
}
$kidCb = {
    param($h, $l)
    $sb = New-Object System.Text.StringBuilder 256
    [void][DsWin3]::GetClassName($h, $sb, 256)
    $tb = New-Object System.Text.StringBuilder 256
    [void][DsWin3]::GetWindowText($h, $tb, 256)
    $pidv = 0
    [void][DsWin3]::GetWindowThreadProcessId($h, [ref]$pidv)
    [DsWin3]::Kids.Add(("0x{0:X}|{1}|{2}|{3}|{4}" -f $h.ToInt64(), $sb.ToString(), $tb.ToString(), $pidv, [DsWin3]::IsWindowVisible($h)))
    return $true
}

[void][DsWin3]::EnumWindows($topCb, [IntPtr]::Zero)

Write-Output "===== FindWindow behavior test ====="
function Try-Find([string]$label, [string]$cls, [string]$name) {
    [DsWin3]::GetLastError() | Out-Null
    $r = [DsWin3]::FindWindow($cls, $name)
    $err = [DsWin3]::GetLastError()
    Write-Output ("  {0,-46} => 0x{1:X} (lastError={2})" -f $label, $r.ToInt64(), $err)
}
Try-Find "FindWindow('Progman', null)" 'Progman' $null
Try-Find "FindWindow('Progman', 'Program Manager')" 'Progman' 'Program Manager'
Try-Find "FindWindow('Progman', '')" 'Progman' ''
Try-Find "FindWindow(null, 'Program Manager')" $null 'Program Manager'
Try-Find "FindWindow('Shell_TrayWnd', null)" 'Shell_TrayWnd' $null
Try-Find "FindWindow('Shell_TrayWnd', '')" 'Shell_TrayWnd' ''

Write-Output "===== Progman + children (from EnumWindows) ====="
$progmanLine = $null
foreach ($line in [DsWin3]::Top) { if ($line.Split('|')[1] -eq 'Progman') { $progmanLine = $line; break } }
if ($null -eq $progmanLine) {
    Write-Output "  Progman NOT in EnumWindows list!"
} else {
    $pmHwnd = [IntPtr]::new([Convert]::ToInt64($progmanLine.Split('|')[0].Substring(2), 16))
    Write-Output ("  Progman: {0}" -f $progmanLine)
    [DsWin3]::Kids.Clear()
    [void][DsWin3]::EnumChildWindows($pmHwnd, $kidCb, [IntPtr]::Zero)
    Write-Output ("  Progman children ({0}):" -f [DsWin3]::Kids.Count)
    foreach ($k in [DsWin3]::Kids) { Write-Output ("    " + $k) }
    $dv = [DsWin3]::FindWindowEx($pmHwnd, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
    Write-Output ("  FindWindowEx(progman,0,'SHELLDLL_DefView',null) = 0x{0:X}" -f $dv.ToInt64())
}

Write-Output "===== All WorkerW + children ====="
$workerLines = @()
foreach ($line in [DsWin3]::Top) { if ($line.Split('|')[1] -eq 'WorkerW') { $workerLines += $line } }
Write-Output ("  WorkerW count: {0}" -f $workerLines.Count)
foreach ($line in $workerLines) {
    $hw = [IntPtr]::new([Convert]::ToInt64($line.Split('|')[0].Substring(2), 16))
    [DsWin3]::Kids.Clear()
    [void][DsWin3]::EnumChildWindows($hw, $kidCb, [IntPtr]::Zero)
    $hasDv = $false
    foreach ($k in [DsWin3]::Kids) { if ($k.Split('|')[1] -eq 'SHELLDLL_DefView') { $hasDv = $true } }
    Write-Output ("  {0} children={1} hasDefView={2}" -f $line, [DsWin3]::Kids.Count, $hasDv)
    if ($hasDv) {
        foreach ($k in [DsWin3]::Kids) { Write-Output ("      " + $k) }
    }
}
