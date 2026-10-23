﻿# 诊断：在托盘溢出飞出窗口区域内网格扫描，找出真正能弹出 DeskSpace 托盘菜单的坐标
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace DsWin6 {
    public static class N {
        public static System.Collections.Generic.List<string> Wins = new System.Collections.Generic.List<string>();
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(int x, int y);
        public const uint MouseRightDown = 0x0008;
        public const uint MouseRightUp = 0x0010;
        public const uint MouseLeftDown = 0x0002;
        public const uint MouseLeftUp = 0x0004;
        public static string ClassPidOf(IntPtr h) {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, 256);
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            return sb.ToString() + "#" + h.ToInt64() + "#" + pid;
        }
        public static string RectOf(IntPtr h) {
            RECT r;
            if (!GetWindowRect(h, out r)) { return "0,0,0,0"; }
            return string.Format("{0},{1},{2},{3}", r.Left, r.Top, r.Right, r.Bottom);
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }
}
"@

$cb = { param($h, $l) [DsWin6.N]::Wins.Add([DsWin6.N]::ClassPidOf($h)); return $true }
function Enum-All {
    [DsWin6.N]::Wins.Clear()
    [void][DsWin6.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWin6.N]::Wins
}
function Test-MenuPresent([int]$ownerPid) {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('#')
        if (($p[0] -eq "#32768") -and ([int]$p[2] -eq $ownerPid)) { return $true }
    }
    return $false
}

$proc = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskSpace 未运行"; exit }

$fly = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('#')
    if ($p[0] -eq "TopLevelWindowForOverflowXamlIsland") { $fly = [IntPtr]::new([Convert]::ToInt64($p[1], 10)) }
}
if ($fly -eq [IntPtr]::Zero) { Write-Host "未打开溢出飞出窗口"; exit }
$fr = ([DsWin6.N]::RectOf($fly)).Split(',')
$l = [int]$fr[0]; $t = [int]$fr[1]; $r = [int]$fr[2]; $b = [int]$fr[3]
Write-Host ("飞出窗口矩形 = ({0},{1})-({2},{3})" -f $l, $t, $r, $b)

$hit = $false
$win = $null
for ($y = ($t + 6); ($y -lt ($b - 6)) -and (-not $hit); $y += 10) {
    for ($x = ($l + 6); ($x -lt ($r - 6)) -and (-not $hit); $x += 10) {
        [DsWin6.N]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 20
        [DsWin6.N]::mouse_event([DsWin6.N]::MouseRightDown, 0, 0, 0, [IntPtr]::Zero)
        [DsWin6.N]::mouse_event([DsWin6.N]::MouseRightUp, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        if (Test-MenuPresent $proc.Id) {
            Write-Host ("命中坐标 = ({0},{1})" -f $x, $y)
            $hit = $true
            $win = "$x,$y"
            break
        }
    }
}
if (-not $hit) { Write-Host "整个飞出窗口区域内没有能弹出菜单的坐标" }
Write-Host "done"
