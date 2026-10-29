# 诊断：把 UIA 物理像素坐标按 DPI 缩放换算成逻辑坐标后，能否真的右键弹出托盘菜单
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace DsWin5 {
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
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
        public const uint MouseRightDown = 0x0008;
        public const uint MouseRightUp = 0x0010;
        public static double GetDpiScale() {
            IntPtr dc = GetDC(IntPtr.Zero);
            int physX = GetDeviceCaps(dc, 118); // DESKTOPHORZRES
            ReleaseDC(IntPtr.Zero, dc);
            int logX = GetSystemMetrics(0);     // SM_CXSCREEN
            if (logX <= 0) { return 1.0; }
            return (double)physX / (double)logX;
        }
        public static string ClassPidOf(IntPtr h) {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, 256);
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            return sb.ToString() + "#" + h.ToInt64() + "#" + pid;
        }
    }
}
"@

$cb = { param($h, $l) [DsWin5.N]::Wins.Add([DsWin5.N]::ClassPidOf($h)); return $true }
function Enum-All {
    [DsWin5.N]::Wins.Clear()
    [void][DsWin5.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWin5.N]::Wins
}
function Find-IconRoot() {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('#')
        if ($p[0] -eq "TopLevelWindowForOverflowXamlIsland") {
            return [IntPtr]::new([Convert]::ToInt64($p[1], 10))
        }
    }
    return [IntPtr]::Zero
}

$scale = [DsWin5.N]::GetDpiScale()
Write-Host ("DPI 缩放系数 = {0}" -f $scale)

$proc = Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskLanes 未运行"; exit }
Write-Host ("DeskLanes PID = {0}" -f $proc.Id)

$h = Find-IconRoot
if ($h -eq [IntPtr]::Zero) { Write-Host "溢出飞出窗口未打开"; exit }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$target = $null
foreach ($e in $all) {
    $n = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
    if (($n -is [string]) -and ($n -like "*DeskLanes*") -and ($n -notlike "*运行窗口*")) { $target = $e; break }
}
if ($target -eq $null) { Write-Host "溢出区里没有 DeskLanes 图标"; exit }

$rect = $target.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty)
$xRaw = [int]($rect.X + $rect.Width / 2)
$yRaw = [int]($rect.Y + $rect.Height / 2)
$x = [int]($xRaw / $scale)
$y = [int]($yRaw / $scale)
Write-Host ("物理坐标 = ({0},{1})  换算后逻辑坐标 = ({2},{3})" -f $xRaw, $yRaw, $x, $y)

[DsWin5.N]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 120
[DsWin5.N]::mouse_event([DsWin5.N]::MouseRightDown, $x, $y, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[DsWin5.N]::mouse_event([DsWin5.N]::MouseRightUp, $x, $y, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 800

Write-Host "=== 查找 #32768 菜单（按 PID 归属过滤） ==="
$found = $false
foreach ($line in (Enum-All)) {
    $p = $line.Split('#')
    if (($p[0] -eq "#32768") -and ([int]$p[2] -eq $proc.Id)) {
        Write-Host ("  命中：句柄=0x{0:X} 可见={1} PID={2}" -f [Convert]::ToInt64($p[1], 10), [DsWin5.N]::IsWindowVisible([IntPtr]::new([Convert]::ToInt64($p[1], 10))), $p[2])
        $found = $true
    }
}
if (-not $found) { Write-Host "  未找到归属于 DeskLanes 的菜单窗口" }
Write-Host "done"
