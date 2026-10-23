# 诊断：Win11 托盘溢出区里 DeskSpace 图标到底是哪一个 UIA 元素
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace DsWin3 {
    public static class N {
        public static System.Collections.Generic.List<string> Wins = new System.Collections.Generic.List<string>();
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        public static string DescribeWin(IntPtr h) {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, 256);
            var tb = new System.Text.StringBuilder(256);
            GetWindowText(h, tb, 256);
            return string.Format("0x{0:X}|{1}|{2}|{3}", h.ToInt64(), sb.ToString(), tb.ToString(), IsWindowVisible(h));
        }
    }
}
"@

$cb = { param($h, $l) [DsWin3.N]::Wins.Add([DsWin3.N]::DescribeWin($h)); return $true }
function Enum-All {
    [DsWin3.N]::Wins.Clear()
    [void][DsWin3.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWin3.N]::Wins
}
function Find-Class([string]$cls) {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if ($p[1] -eq $cls) { return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16)) }
    }
    return [IntPtr]::Zero
}
function Describe($el) {
    $n = $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
    $cls = $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ClassNameProperty)
    $ct = $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ControlTypeProperty)
    $aid = $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
    if ($cls -isnot [string]) { $cls = "" }
    if ($aid -isnot [string]) { $aid = "" }
    $clickable = $false
    $pt = [System.Windows.Point]::new(0, 0)
    try { $clickable = $el.TryGetClickablePoint([ref]$pt) } catch { $clickable = $false }
    $pos = if ($clickable) { "($([int]$pt.X),$([int]$pt.Y))" } else { "no-point" }
    return ("  name=[{0}] class=[{1}] aid=[{2}] type=[{3}] {4}" -f $n, $cls, $aid, $ct.ProgrammaticName, $pos)
}

Write-Host "=== 顶层窗口 ==="
foreach ($line in (Enum-All)) {
    $p = $line.Split('|')
    if ($p[3] -eq "True") { Write-Host ("  {0}" -f $line) }
}

$tray = Find-Class "Shell_TrayWnd"
Write-Host ""
Write-Host "=== Shell_TrayWnd 子树中含 DeskSpace 的元素 ==="
if ($tray -ne [IntPtr]::Zero) {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($tray)
    $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        $n = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        if (($n -is [string]) -and ($n -like "*DeskSpace*")) { Write-Host (Describe $e) }
    }
}

Write-Host ""
Write-Host "=== 打开溢出区，列出飞出面板中含 DeskSpace 的元素 ==="
$roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty,
        "TopLevelWindowForOverflowXamlIsland")))
if ($roots.Count -eq 0) {
    $trayEl2 = [System.Windows.Automation.AutomationElement]::FromHandle($tray)
    $condBtn = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $trayEl2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condBtn)) {
        $n = $b.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        if (($n -is [string]) -and ($n -like "*隐藏的图标*")) { $chevron = $b; break }
    }
    if ($chevron -eq $null) {
        Write-Host "  未找到 chevron 按钮"
    } else {
        $ip = $chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke()
        Start-Sleep -Milliseconds 1200
        $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ClassNameProperty,
                "TopLevelWindowForOverflowXamlIsland")))
    }
} else {
    Write-Host "  溢出飞出窗口已经打开，直接复用"
}
Write-Host ("  飞出窗口数量：{0}" -f $roots.Count)
    if ($roots.Count -gt 0) {
        $all = $roots[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        Write-Host ("  飞出面板元素总数：{0}" -f $all.Count)
        foreach ($e in $all) {
            $n = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
            if (($n -is [string]) -and ($n.Length -gt 0) -and ($n -like "*DeskSpace*")) { Write-Host (Describe $e) }
        }
        Write-Host "  --- 全部 Button 型元素（含空名）前 40 个 ---"
        $i = 0
        foreach ($e in $roots[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, $condBtn)) {
            if ($i -ge 40) { break }
            Write-Host (Describe $e)
            $i++
        }
}
Write-Host "done"
