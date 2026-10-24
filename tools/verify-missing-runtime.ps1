#Requires -Version 5.1
<#
  verify-missing-runtime.ps1 —— 阶段 7 授权测试第 4 项：缺运行时场景的启动表现

  本机无法真正卸载 .NET 10 Desktop Runtime，能做的两种模拟：
    A. DOTNET_ROOT 指向空目录 —— 框架解析的第一优先路径被破坏，
       观察 apphost 是回退到系统安装、还是报错（验证健壮性）
    B. 正常启动（对照组）
  若出现"缺少运行时"错误对话框，捕获其标题/文本作为证据。

  安全：只启动/结束 DeskSpace 自己；不改系统设置、不动注册表。
#>
param(
    [string]$ExePath = 'C:\Users\TTjustTT\Documents\Github\DeskSpace\publish\win-x64\DeskSpace.exe',
    [string]$OutDir  = 'C:\Users\TTjustTT\Documents\Github\DeskSpace\docs\stage7-evidence'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

public static class MR
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);

    public static ArrayList List(uint targetPid)
    {
        var list = new ArrayList();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid) {
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                var c = new StringBuilder(256); GetClassName(h, c, 256);
                list.Add(string.Format("0x{0:X} vis={1} class={2} title={3}",
                    h.ToInt64(), IsWindowVisible(h), c, t));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

function Kill-DeskSpace {
    Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

function Test-Launch([string]$label, [hashtable]$envVars) {
    Write-Host "---- $label ----"
    Kill-DeskSpace

    # 通过当前进程环境变量传给子进程（ProcessStartInfo.EnvironmentVariables 在部分环境下为 null）
    $saved = @{}
    foreach ($k in $envVars.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k)
        [Environment]::SetEnvironmentVariable($k, $envVars[$k])
    }
    try {
        $p = Start-Process -FilePath $ExePath -PassThru
    } finally {
        foreach ($k in $saved.Keys) {
            [Environment]::SetEnvironmentVariable($k, $saved[$k])
        }
    }
    Start-Sleep -Seconds 8
    $p.Refresh()

    Write-Host ("    HasExited = {0}" -f $p.HasExited)
    if (-not $p.HasExited) {
        Write-Host ("    ExitCode  = (still running)")
        $wins = [MR]::List($p.Id)
        Write-Host ("    顶层窗口数 = {0}" -f $wins.Count)
        foreach ($w in $wins) { Write-Host ("        {0}" -f $w) }
    } else {
        Write-Host ("    ExitCode  = {0}" -f $p.ExitCode)
    }
    return $p
}

Write-Output '===== 缺运行时场景启动表现 ====='

# A. DOTNET_ROOT 指向空目录
$pA = Test-Launch 'A. DOTNET_ROOT 指向空目录' @{ 'DOTNET_ROOT' = 'C:\__definitely_not_a_dotnet_dir__' }
if (-not $pA.HasExited) { Stop-Process -Id $pA.Id -Force; Start-Sleep -Milliseconds 500 }

# B. 正常启动（对照）
$pB = Test-Launch 'B. 正常启动（对照组）' @{}
if (-not $pB.HasExited) { Stop-Process -Id $pB.Id -Force; Start-Sleep -Milliseconds 500 }

Kill-DeskSpace
Write-Output '===== 测试4 结束 ====='
