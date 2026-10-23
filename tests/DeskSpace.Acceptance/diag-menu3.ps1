# 诊断：DeskSpace 原生托盘菜单（#32768）里的菜单项，UIA 到底能看到什么
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace DsWin7 {
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
        public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
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

$cb = { param($h, $l) [DsWin7.N]::Wins.Add([DsWin7.N]::ClassPidOf($h)); return $true }
function Enum-All {
    [DsWin7.N]::Wins.Clear()
    [void][DsWin7.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWin7.N]::Wins
}

$proc = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskSpace 未运行"; exit }
Write-Host ("DeskSpace PID = {0}" -f $proc.Id)

# 找到消息宿主窗口（标题 DeskSpaceMessageHost，由 AppController 创建）
$host1 = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('#')
    if ($p[0] -match "HwndWrapper|DeskSpaceMessageHost") {
        Write-Host ("候选窗口 class=[{0}] pid={1}" -f $p[0], $p[2])
    }
}
Write-Host "--- 用 EnumWindows 找到的 DeskSpace 进程窗口 ---"
foreach ($line in (Enum-All)) {
    $p = $line.Split('#')
    if ([int]$p[2] -eq $proc.Id) { Write-Host ("  class=[{0}] handle=0x{1:X} visible={2}" -f $p[0], [Convert]::ToInt64($p[1], 10), [DsWin7.N]::IsWindowVisible([IntPtr]::new([Convert]::ToInt64($p[1], 10)))) }
}

# 直接给主窗口发托盘回调消息不一定行，先试宿主：用标题找
Add-Type -AssemblyName PresentationCore
Write-Host "--- 尝试通过 HwndWrapper 宿主发消息 ---"
$titles = @{}
foreach ($line in (Enum-All)) {
    $p = $line.Split('#')
    if ([int]$p[2] -eq $proc.Id -and ($p[0] -like "HwndWrapper*")) {
        $h = [IntPtr]::new([Convert]::ToInt64($p[1], 10))
        Write-Host ("  向 HwndWrapper 0x{0:X} 发托盘回调消息" -f $h.ToInt64())
        [DsWin7.N]::PostMessage($h, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
        Start-Sleep -Milliseconds 700
        break
    }
}

Write-Host "--- 查找 #32768 菜单并枚举元素 ---"
$menuHwnd = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('#')
    if (($p[0] -eq "#32768") -and ([int]$p[2] -eq $proc.Id)) { $menuHwnd = [IntPtr]::new([Convert]::ToInt64($p[1], 10)) }
}
if ($menuHwnd -eq [IntPtr]::Zero) {
    Write-Host "  没有找到 DeskSpace 的菜单窗口"
} else {
    Write-Host ("  菜单窗口 0x{0:X} 矩形={1}" -f $menuHwnd.ToInt64(), [DsWin7.N]::RectOf($menuHwnd))
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($menuHwnd)
    Write-Host ("  根元素 Name=[{0}] ClassName=[{1}]" -f `
        $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty), `
        $el.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ClassNameProperty))
    $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Host ("  后代元素数量 = {0}" -f $all.Count)
    foreach ($e in $all) {
        $n = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        $ct = $e.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ControlTypeProperty)
        Write-Host ("    name=[{0}] type={1}" -f $n, $ct.ProgrammaticName)
    }
}
Write-Host "done"
