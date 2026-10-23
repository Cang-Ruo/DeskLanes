#Requires -Version 5.1
<#
.SYNOPSIS
  DeskSpace 阶段 0 · 桌面图标隐藏/恢复机制实验脚本（需你亲自确认后运行）
.DESCRIPTION
  通过 ShowWindow 切换 Progman > SHELLDLL_DefView > SysListView32
  （桌面图标列表窗口）的可见性，验证"原生桌面图标隐藏/恢复"方案是否可行。
  不写注册表、不重启 Explorer；图标窗口由 Explorer 托管，若 Explorer 意外
  重启，图标会自动恢复显示。
.WARNING
  1. 运行时会短暂隐藏桌面图标 3 秒，随后自动恢复；
  2. 请先运行 survey-desktop-windows.ps1 确认窗口链存在，再运行本脚本；
  3. 若恢复失败，重新运行本脚本或重启 Explorer 即可恢复。
.NOTES
  用法：powershell -ExecutionPolicy Bypass -File .\stage0\test-desktop-icon-toggle.ps1
#>
$ErrorActionPreference = 'Stop'

$source = @"
using System;
using System.Runtime.InteropServices;
public static class IconToggle {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@
Add-Type -TypeDefinition $source

$SW_HIDE = 0
$SW_SHOW = 5

$progman = [IconToggle]::FindWindow('Progman', $null)
$defView = [IconToggle]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
$listView = [IconToggle]::FindWindowEx($defView, [IntPtr]::Zero, 'SysListView32', $null)

"Progman          : 0x$($progman.ToInt64().ToString('X'))"
"SHELLDLL_DefView : 0x$($defView.ToInt64().ToString('X'))"
"SysListView32    : 0x$($listView.ToInt64().ToString('X'))"

if ($listView -eq [IntPtr]::Zero) {
    "未找到桌面图标列表窗口，本次不做任何修改。"
    exit 1
}

$visibleBefore = [IconToggle]::IsWindowVisible($listView)
"切换前可见性: $visibleBefore"
if (-not $visibleBefore) {
    "图标当前不可见，跳过实验（避免破坏你现有的显示状态）。"
    exit 0
}

try {
    "1) 隐藏桌面图标（3 秒）..."
    [IconToggle]::ShowWindow($listView, $SW_HIDE) | Out-Null
    Start-Sleep -Seconds 3
    "2) 恢复桌面图标..."
    [IconToggle]::ShowWindow($listView, $SW_SHOW) | Out-Null
    Start-Sleep -Milliseconds 500
}
finally {
    if (-not [IconToggle]::IsWindowVisible($listView)) {
        "finally: 检测到未恢复，强制执行恢复"
        [IconToggle]::ShowWindow($listView, $SW_SHOW) | Out-Null
        Start-Sleep -Milliseconds 300
    }
}

$visibleAfter = [IconToggle]::IsWindowVisible($listView)
"切换后可见性: $visibleAfter"
if ($visibleAfter) {
    "结论: 隐藏/恢复机制在本机可用（后续还需验证多显示器、DPI 缩放、Explorer 重启等场景）"
} else {
    "结论: 恢复失败！如果图标没有回来，请重新运行本脚本或重启 Explorer。"
}
