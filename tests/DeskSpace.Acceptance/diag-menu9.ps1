﻿# 诊断：先 SetForegroundWindow 激活 #32768 菜单，再键盘导航选中指定项，看是否稳定生效
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace DsWinD {
    public static class N {
        public static List<string> Wins = new List<string>();
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        public const byte VkDown = 0x28;
        public const byte VkReturn = 0x0D;
        public const byte VkEscape = 0x1B;
        public const uint KeyUp = 0x0002;
        public static string Describe(IntPtr h) {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            var tb = new StringBuilder(256);
            GetWindowText(h, tb, 256);
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), IsWindowVisible(h), pid);
        }
        public static void Press(byte vk) {
            keybd_event(vk, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(30);
            keybd_event(vk, 0, KeyUp, IntPtr.Zero);
            System.Threading.Thread.Sleep(30);
        }
    }
}
"@

$cb = { param($h, $l) [DsWinD.N]::Wins.Add([DsWinD.N]::Describe($h)); return $true }
function Enum-All {
    [DsWinD.N]::Wins.Clear()
    [void][DsWinD.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWinD.N]::Wins
}
function Find-Row([string]$cls, [string]$title, [int]$ownerPid) {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if ([int]$p[4] -eq $ownerPid) {
            if ((($cls -eq $null) -or ($cls -eq "") -or ($p[1] -eq $cls)) -and
                (($title -eq $null) -or ($title -eq "") -or ($p[2] -eq $title))) {
                return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16))
            }
        }
    }
    return [IntPtr]::Zero
}

$proc = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskSpace 未运行"; exit }
$hostH = Find-Row "" "DeskSpaceMessageHost" $proc.Id
Write-Host ("宿主窗口 = 0x{0:X}" -f $hostH.ToInt64())

function Open-Menu {
    [DsWinD.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
    Start-Sleep -Milliseconds 700
    return (Find-Row "#32768" $null $proc.Id)
}

function Show-State([string]$label) {
    $titles = @()
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if (([int]$p[4] -eq $proc.Id) -and ($p[3] -eq "True") -and ($p[1] -like "Window*")) {
            $titles += ("{0}=[{1}]" -f $p[1], $p[2])
        }
    }
    Write-Host ("  {0}: {1}" -f $label, ($titles -join " "))
}

foreach ($index in @(2, 3, 4, 5)) {
    Write-Host ("=== 选中第 {0} 个可导航菜单项 ===" -f $index)
    $menuH = Open-Menu
    if ($menuH -eq [IntPtr]::Zero) { Write-Host "  菜单未弹出"; continue }
    Write-Host ("  菜单窗口 0x{0:X}，前台窗口 0x{1:X}" -f $menuH.ToInt64(), [DsWinD.N]::GetForegroundWindow().ToInt64())
    $fg = [DsWinD.N]::SetForegroundWindow($menuH)
    Start-Sleep -Milliseconds 250
    Write-Host ("  SetForegroundWindow={0}，当前前台 0x{1:X}" -f $fg, [DsWinD.N]::GetForegroundWindow().ToInt64())
    for ($i = 1; $i -le $index; $i++) { [DsWinD.N]::Press([DsWinD.N]::VkDown) }
    Start-Sleep -Milliseconds 150
    $still = (Find-Row "#32768" $null $proc.Id) -ne [IntPtr]::Zero
    Write-Host ("  导航后菜单仍然存在：{0}" -f $still)
    [DsWinD.N]::Press([DsWinD.N]::VkReturn)
    Start-Sleep -Milliseconds 1200
    Show-State "选中后"
    [DsWinD.N]::Press([DsWinD.N]::VkEscape)
}
Write-Host "done"
