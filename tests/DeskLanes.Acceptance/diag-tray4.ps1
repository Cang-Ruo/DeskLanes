# 诊断：托盘溢出区 DeskLanes 图标的 BoundingRectangle 是否可用（替代 GetClickablePoint）
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace DsWin4 {
    public static class N {
        public static System.Collections.Generic.List<string> Wins = new System.Collections.Generic.List<string>();
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
        public static string ClassOf(IntPtr h) {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
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

$cb = { param($h, $l) [DsWin4.N]::Wins.Add([DsWin4.N]::ClassOf($h) + "#" + $h.ToInt64()); return $true }
[DsWin4.N]::Wins.Clear()
[void][DsWin4.N]::EnumWindows($cb, [IntPtr]::Zero)
$h = [IntPtr]::Zero
foreach ($line in [DsWin4.N]::Wins) {
    $parts = $line.Split('#')
    if ($parts[0] -eq "TopLevelWindowForOverflowXamlIsland") { $h = [IntPtr]::new([Convert]::ToInt64($parts[1], 10)) }
}
if ($h -eq [IntPtr]::Zero) {
    Write-Host "溢出飞出窗口未打开，请先在任务栏点开「显示隐藏的图标」"
    exit
}
Write-Host ("飞出窗口句柄=0x{0:X} 可见={1} 矩形={2}" -f $h.ToInt64(), [DsWin4.N]::IsWindowVisible($h), [DsWin4.N]::RectOf($h))

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($e in $all) {
    $n = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
    if (($n -is [string]) -and ($n -like "*DeskLanes*") -and ($n -notlike "*运行窗口*")) {
        $rect = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty)
        Write-Host ("name=[{0}]" -f $n)
        Write-Host ("  BoundingRectangle = {0}" -f $rect)
        Write-Host ("  X={0} Y={1} W={2} H={3}" -f $rect.X, $rect.Y, $rect.Width, $rect.Height)
        $pt = [System.Windows.Point]::new(0, 0)
        Write-Host ("  TryGetClickablePoint = {0}" -f $e.TryGetClickablePoint([ref]$pt))
    }
}
Write-Host "done"
