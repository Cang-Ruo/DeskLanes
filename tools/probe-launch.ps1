#Requires -Version 5.1
<#
  probe-launch.ps1 —— 启动指定 exe，记录：进程、可见窗口、单文件解压目录内容

  用途：确认自包含版是否真的自带运行时（coreclr.dll 出现在解压目录里），
        以及便携版是否不含它；顺便给出"窗口到底有没有出来"的准确判断
        （Process.MainWindowHandle 对隐藏主窗口的 WPF 程序会返回 0，不可靠）。

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File probe-launch.ps1 -Exe <路径>
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Wz {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
}
'@

function Say($m) { Write-Host $m }

$extractRoot = Join-Path $env:TEMP '.net\DeskSpace'
if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue }

$name = [System.IO.Path]::GetFileNameWithoutExtension($Exe)
Say ('启动: ' + $Exe)
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 15

$procs = Get-Process -Name $name -ErrorAction SilentlyContinue
Say ('进程数: ' + @($procs).Count + '  PID: ' + (($procs | ForEach-Object { $_.Id }) -join ','))

$script:hwnds = @()
$cb = [Wz+EnumWindowsProc]{
    param($h, $l)
    $p2 = 0
    $null = [Wz]::GetWindowThreadProcessId($h, [ref]$p2)
    if ($p2 -eq $pidTarget) {
        if ([Wz]::IsWindowVisible($h)) {
            $len = [Wz]::GetWindowTextLength($h)
            $sb = New-Object System.Text.StringBuilder ($len + 1)
            $null = [Wz]::GetWindowText($h, $sb, $sb.Capacity)
            $script:hwnds += ($h.ToString() + ' | ' + $sb.ToString())
        }
    }
    return $true
}

foreach ($proc in $procs) {
    $pidTarget = [uint32]$proc.Id
    $script:hwnds = @()
    $null = [Wz]::EnumWindows($cb, [IntPtr]::Zero)
    Say ('PID ' + $proc.Id + ' 可见顶层窗口数: ' + @($script:hwnds).Count)
    foreach ($w in $script:hwnds) { Say ('    ' + $w) }
    Say ('    MainWindowHandle(参考) = ' + $proc.MainWindowHandle)
}

Say '--- 单文件解压目录 ---'
$netAll = Join-Path $env:TEMP '.net'
if (Test-Path -LiteralPath $netAll) {
    Get-ChildItem -LiteralPath $netAll -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 40 |
        ForEach-Object { Say ('    ' + $_.FullName) }
    $core = Get-ChildItem -LiteralPath $netAll -Recurse -Filter 'coreclr.dll' -ErrorAction SilentlyContinue
    $wpf  = Get-ChildItem -LiteralPath $netAll -Recurse -Filter 'wpfgfx_cor3.dll' -ErrorAction SilentlyContinue
    Say ('coreclr.dll    : ' + (@($core).Count) + ' 个' + ' ' + $(if ($core) { ($core | Select-Object -First 1).FullName } else { '' }))
    Say ('wpfgfx_cor3.dll: ' + (@($wpf).Count) + ' 个' + ' ' + $(if ($wpf) { ($wpf | Select-Object -First 1).FullName } else { '' }))
} else {
    Say '    %TEMP%\.net 不存在（没有发生解压）'
}

Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Say '已结束进程。'
