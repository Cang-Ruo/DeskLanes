# 诊断：DeskSpace 原生托盘菜单（#32768）里的菜单项究竟能否被枚举
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace DsWin8 {
    public static class N {
        public static System.Collections.Generic.List<string> Wins = new System.Collections.Generic.List<string>();
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
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
        public static string Describe(IntPtr h) {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            var tb = new StringBuilder(256);
            GetWindowText(h, tb, 256);
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), IsWindowVisible(h), pid);
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

$cb = { param($h, $l) [DsWin8.N]::Wins.Add([DsWin8.N]::Describe($h)); return $true }
function Enum-All {
    [DsWin8.N]::Wins.Clear()
    [void][DsWin8.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWin8.N]::Wins
}
function Find-Row([string]$title, [int]$ownerPid) {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if (($p[2] -eq $title) -and ([int]$p[4] -eq $ownerPid)) {
            return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16))
        }
    }
    return [IntPtr]::Zero
}

$proc = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskSpace 未运行"; exit }
Write-Host ("DeskSpace PID = {0}" -f $proc.Id)

$hostH = Find-Row "DeskSpaceMessageHost" $proc.Id
Write-Host ("消息宿主窗口 = 0x{0:X}" -f $hostH.ToInt64())
if ($hostH -eq [IntPtr]::Zero) { Write-Host "找不到宿主窗口"; exit }

[DsWin8.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 800

$menuH = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('|')
    if (($p[1] -eq "#32768") -and ([int]$p[4] -eq $proc.Id)) { $menuH = [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16)) }
}
if ($menuH -eq [IntPtr]::Zero) { Write-Host "菜单未弹出"; exit }
Write-Host ("菜单窗口 = 0x{0:X} 矩形 = {1}" -f $menuH.ToInt64(), [DsWin8.N]::RectOf($menuH))

$el = [System.Windows.Automation.AutomationElement]::FromHandle($menuH)
Write-Host ("根元素 ClassName=[{0}] ControlType=[{1}]" -f `
    $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ClassNameProperty), `
    $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ControlTypeProperty).ProgrammaticName)

$all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Host ("TrueCondition 后代数量 = {0}" -f $all.Count)
foreach ($e in $all) {
    $n = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
    $ct = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ControlTypeProperty)
    Write-Host ("    name=[{0}] type={1}" -f $n, $ct.ProgrammaticName)
}

$condMenuItem = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::MenuItem)
$items = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condMenuItem)
Write-Host ("MenuItem 后代数量 = {0}" -f $items.Count)
foreach ($e in $items) {
    $n = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
    Write-Host ("    menuItem=[{0}]" -f $n)
}
Write-Host "done"
