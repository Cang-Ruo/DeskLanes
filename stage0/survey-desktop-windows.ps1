#Requires -Version 5.1
<#
.SYNOPSIS
  DeskSpace 阶段 0 · 桌面窗口结构只读勘察
.DESCRIPTION
  仅读取窗口类名、可见性、进程 ID，不调用 ShowWindow 等任何会改变窗口状态的 API。
  用于确认"原生桌面图标隐藏/恢复"功能的目标窗口链是否存在。
.NOTES
  用法（在 DeskSpace 仓库根目录执行）：
    powershell -ExecutionPolicy Bypass -File .\stage0\survey-desktop-windows.ps1
#>
$ErrorActionPreference = 'Stop'

$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class DeskSpaceSurvey {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static string ClassName(IntPtr hWnd) {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
    public static string Hex(IntPtr hWnd) {
        return "0x" + hWnd.ToInt64().ToString("X");
    }
}
"@

Add-Type -TypeDefinition $source

function Show-WindowInfo {
    param([Parameter(Mandatory = $true)][IntPtr]$Handle, [string]$Label)
    if ($Handle -eq [IntPtr]::Zero) {
        "  {0,-24} = <不存在>" -f $Label
        return
    }
    $procId = 0
    [DeskSpaceSurvey]::GetWindowThreadProcessId($Handle, [ref]$procId) | Out-Null
    "  {0,-24} = {1}  类名={2}  可见={3}  进程PID={4}" -f $Label, [DeskSpaceSurvey]::Hex($Handle), [DeskSpaceSurvey]::ClassName($Handle), [DeskSpaceSurvey]::IsWindowVisible($Handle), $procId
}

"=========================================================="
" DeskSpace 阶段 0 · 桌面窗口结构只读勘察"
" 时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
"=========================================================="

"`n== 1. Shell 主窗口 =="
$shellWindow = [DeskSpaceSurvey]::GetShellWindow()
Show-WindowInfo -Handle $shellWindow -Label "GetShellWindow"

"`n== 2. Progman（桌面）窗口层级 =="
$progman = [DeskSpaceSurvey]::FindWindow('Progman', $null)
Show-WindowInfo -Handle $progman -Label "Progman"

if ($progman -ne [IntPtr]::Zero) {
    $children = New-Object System.Collections.ArrayList
    $enumChildren = [DeskSpaceSurvey+EnumWindowsProc]{
        param($hWnd, $lParam)
        $script:children.Add($hWnd) | Out-Null
        return $true
    }
    [DeskSpaceSurvey]::EnumChildWindows($progman, $enumChildren, [IntPtr]::Zero) | Out-Null
    "  Progman 直接子窗口（共 $($children.Count) 个）:"
    foreach ($c in $children) {
        "    - 类名=$([DeskSpaceSurvey]::ClassName($c)) 可见=$([DeskSpaceSurvey]::IsWindowVisible($c)) $([DeskSpaceSurvey]::Hex($c))"
    }
}

"`n== 3. 桌面图标列表窗口链（隐藏/恢复功能的关键目标） =="
$defView = [DeskSpaceSurvey]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
Show-WindowInfo -Handle $defView -Label "SHELLDLL_DefView"
if ($defView -ne [IntPtr]::Zero) {
    $listView = [DeskSpaceSurvey]::FindWindowEx($defView, [IntPtr]::Zero, 'SysListView32', $null)
    Show-WindowInfo -Handle $listView -Label "SysListView32(图标列表)"
    if ($listView -eq [IntPtr]::Zero) {
        "  !! 未找到 SysListView32，需要进一步排查（见下方 WorkerW 分支）"
    }
} else {
    "  !! Progman 下没有 SHELLDLL_DefView，可能在 WorkerW 分支，见下方枚举"
}

"`n== 4. WorkerW 分支排查（Win11 某些配置下桌面挂在此分支） =="
$workerWs = New-Object System.Collections.ArrayList
$enumWorker = [DeskSpaceSurvey+EnumWindowsProc]{
    param($hWnd, $lParam)
    if ([DeskSpaceSurvey]::ClassName($hWnd) -eq 'WorkerW') {
        $script:workerWs.Add($hWnd) | Out-Null
    }
    return $true
}
[DeskSpaceSurvey]::EnumWindows($enumWorker, [IntPtr]::Zero) | Out-Null
"  找到 WorkerW 窗口 $($workerWs.Count) 个"
foreach ($w in $workerWs) {
    $dv = [DeskSpaceSurvey]::FindWindowEx($w, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
    if ($dv -ne [IntPtr]::Zero) {
        "  WorkerW $([DeskSpaceSurvey]::Hex($w)) 下存在 SHELLDLL_DefView $([DeskSpaceSurvey]::Hex($dv))"
        $lv = [DeskSpaceSurvey]::FindWindowEx($dv, [IntPtr]::Zero, 'SysListView32', $null)
        Show-WindowInfo -Handle $lv -Label "  WorkerW>SysListView32"
    }
}

"`n== 5. 可见顶层窗口类名统计 =="
$tops = New-Object System.Collections.ArrayList
$enumTops = [DeskSpaceSurvey+EnumWindowsProc]{
    param($hWnd, $lParam)
    if ([DeskSpaceSurvey]::IsWindowVisible($hWnd)) { $script:tops.Add($hWnd) | Out-Null }
    return $true
}
[DeskSpaceSurvey]::EnumWindows($enumTops, [IntPtr]::Zero) | Out-Null
$tops | ForEach-Object { [DeskSpaceSurvey]::ClassName($_) } | Group-Object | Sort-Object Name | ForEach-Object { "  $($_.Name)  x$($_.Count)" }

"`n== 勘察结束（本脚本未修改任何窗口状态） =="
