# 诊断：直接向 #32768 菜单窗口投递 WM_KEYDOWN/UP（不依赖前台焦点）
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace DsWinE {
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
        public const uint WmKeyDown = 0x0100;
        public const uint WmKeyUp = 0x0101;
        public const byte VkDown = 0x28;
        public const byte VkReturn = 0x0D;
        public const byte VkEscape = 0x1B;
        public static string Describe(IntPtr h) {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            var tb = new StringBuilder(256);
            GetWindowText(h, tb, 256);
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), IsWindowVisible(h), pid);
        }
        public static void PostKey(IntPtr h, byte vk) {
            PostMessage(h, WmKeyDown, (IntPtr)vk, IntPtr.Zero);
            System.Threading.Thread.Sleep(40);
            PostMessage(h, WmKeyUp, (IntPtr)vk, IntPtr.Zero);
            System.Threading.Thread.Sleep(40);
        }
    }
}
"@

$cb = { param($h, $l) [DsWinE.N]::Wins.Add([DsWinE.N]::Describe($h)); return $true }
function Enum-All {
    [DsWinE.N]::Wins.Clear()
    [void][DsWinE.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWinE.N]::Wins
}
function Find-Menu([int]$ownerPid) {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if (($p[1] -eq "#32768") -and ([int]$p[4] -eq $ownerPid)) {
            return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16))
        }
    }
    return [IntPtr]::Zero
}
function Find-Host([int]$ownerPid) {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if (($p[2] -eq "DeskLanesMessageHost") -and ([int]$p[4] -eq $ownerPid)) {
            return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16))
        }
    }
    return [IntPtr]::Zero
}
function Show-State([int]$ownerPid, [string]$label) {
    $titles = @()
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if (([int]$p[4] -eq $ownerPid) -and ($p[3] -eq "True")) {
            $titles += ("{0}:[{1}]" -f $p[1], $p[2])
        }
    }
    Write-Host ("  {0} -> {1}" -f $label, ($titles -join " "))
}

$proc = Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskLanes 未运行"; exit }
$hostH = Find-Host $proc.Id
Write-Host ("宿主窗口 = 0x{0:X}" -f $hostH.ToInt64())

Write-Host "=== A：逐次投递 Down，观察菜单存活情况 ==="
[DsWinE.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 700
$m = Find-Menu $proc.Id
Write-Host ("  菜单 = 0x{0:X}" -f $m.ToInt64())
for ($i = 1; $i -le 4; $i++) {
    [DsWinE.N]::PostKey($m, [DsWinE.N]::VkDown)
    $still = (Find-Menu $proc.Id) -ne [IntPtr]::Zero
    Write-Host ("  第 {0} 次 Down 后菜单仍存在：{1}" -f $i, $still)
    if (-not $still) { break }
}
$m2 = Find-Menu $proc.Id
if ($m2 -ne [IntPtr]::Zero) {
    [DsWinE.N]::PostKey($m2, [DsWinE.N]::VkReturn)
    Start-Sleep -Milliseconds 1200
}
Show-State $proc.Id "投递 4 次 Down + Enter 后"

Write-Host "=== B：投递 2 次 Down + Enter（应为悬浮窗模式） ==="
[DsWinE.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 700
$m = Find-Menu $proc.Id
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkDown)
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkDown)
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkReturn)
Start-Sleep -Milliseconds 1500
Show-State $proc.Id "投递 2 次 Down + Enter 后"

Write-Host "=== C：投递 4 次 Down + Enter（应为侧边栏模式） ==="
[DsWinE.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 700
$m = Find-Menu $proc.Id
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkDown)
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkDown)
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkDown)
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkDown)
[DsWinE.N]::PostKey($m, [DsWinE.N]::VkReturn)
Start-Sleep -Milliseconds 1500
Show-State $proc.Id "投递 4 次 Down + Enter 后"

Write-Host "done"
