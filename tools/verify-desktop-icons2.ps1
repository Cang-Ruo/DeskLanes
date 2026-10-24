#Requires -Version 5.1
<#
  verify-desktop-icons2.ps1 —— 桌面图标隐藏/恢复的权威复测（阶段 7 授权测试第 2 项）

  判据（不再依赖截图 MD5）：
    A. SHELLDLL_DefView 的 IsWindowVisible
    B. SysListView32 的 LVM_GETITEMCOUNT（图标数量）

  同时验证：广播 lParam 用 'Environment'（应用当前实现）与 'ImmersiveShell' 的差异。
  安全：只写 HideIcons 一个值，finally 还原；不重启 Explorer；不动桌面文件。
#>
param(
    [string]$OutDir = 'C:\Users\TTjustTT\Documents\Github\DeskSpace\docs\stage7-evidence'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

public static class DI2
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    /// <summary>用 EnumWindows 找 class 指定的顶层窗口（本机 FindWindow 实测不可靠）。</summary>
    public static IntPtr FindTopWindowByClass(string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() == cls) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>在 parent 的子窗口里找 SHELLDLL_DefView。</summary>
    public static IntPtr FindDefView(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() == "SHELLDLL_DefView") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static int ListViewCount(IntPtr listView)
    {
        // LVM_GETITEMCOUNT = 0x1004
        return (int)SendMessage(listView, 0x1004, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool Broadcast(string lparam)
    {
        IntPtr r;
        return SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, lparam, 0x0002, 2000, out r) != IntPtr.Zero;
    }
}
'@

[void][DI2]::SetProcessDPIAware()

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

function Get-RegHideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return $null }
    return [int]$v.HideIcons
}

function Get-DesktopState {
    # 找 Progman（或托管 DefView 的 WorkerW）
    $progman = [DI2]::FindTopWindowByClass('Progman')
    $dv = [IntPtr]::Zero
    if ($progman.ToInt64() -ne 0) {
        # 先找 Progman 直接子窗口（FindWindowEx 对子窗口可靠）
        $dv = [DI2]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', [NullString]::Value)
        if ($dv.ToInt64() -eq 0) { $dv = [DI2]::FindDefView($progman) }
    }
    if ($dv.ToInt64() -eq 0) {
        # 遍历 WorkerW
        $w = [DI2]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', [NullString]::Value)
        while ($w.ToInt64() -ne 0 -and $dv.ToInt64() -eq 0) {
            $dv = [DI2]::FindDefView($w)
            $w = [DI2]::FindWindowEx([IntPtr]::Zero, $w, 'WorkerW', [NullString]::Value)
        }
    }
    if ($dv.ToInt64() -eq 0) {
        return @{ DefView = [IntPtr]::Zero; Visible = $null; Icons = -1 }
    }
    $lv = [DI2]::FindWindowEx($dv, [IntPtr]::Zero, 'SysListView32', [NullString]::Value)
    $count = $(if ($lv.ToInt64() -ne 0) { [DI2]::ListViewCount($lv) } else { -1 })
    return @{ DefView = $dv; Visible = [DI2]::IsWindowVisible($dv); Icons = $count }
}

function Wait-Icons([bool]$wantVisible, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $s = Get-DesktopState
        if ($s.Visible -eq $wantVisible) { return @{ Ok = $true; Ms = $sw.ElapsedMilliseconds; State = $s } }
        Start-Sleep -Milliseconds 200
    }
    return @{ Ok = $false; Ms = $sw.ElapsedMilliseconds; State = (Get-DesktopState) }
}

function Set-HideIcons([int]$val) {
    Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value $val
}

Write-Output '===== 桌面图标隐藏/恢复权威复测 ====='
$orig = Get-RegHideIcons
$origPresent = $null -ne $orig
Write-Output ("基线 HideIcons = {0}" -f $(if ($origPresent) { "$orig" } else { 'ABSENT（默认显示）' }))

$s0 = Get-DesktopState
Write-Output ("[基线] DefView=0x{0:X} 可见={1} 图标数={2}" -f $s0.DefView.ToInt64(), $s0.Visible, $s0.Icons)

try {
    # ---- 隐藏：写 1 + 广播 Environment（应用当前实现） ----
    Write-Output '--- 隐藏：写 1 + 广播 Environment ---'
    Set-HideIcons 1
    $b = [DI2]::Broadcast('Environment')
    $r = Wait-Icons $false 4000
    Write-Output ("    广播={0}；DefView 隐藏={1}（{2} ms）；图标数={3}" -f $b, $r.Ok, $r.Ms, $r.State.Icons)

    # ---- 补一刀 ImmersiveShell（不重写注册表） ----
    Write-Output '--- 补广播 ImmersiveShell ---'
    $b2 = [DI2]::Broadcast('ImmersiveShell')
    $r2 = Wait-Icons $false 4000
    Write-Output ("    广播={0}；DefView 隐藏={1}（{2} ms）；图标数={3}" -f $b2, $r2.Ok, $r2.Ms, $r2.State.Icons)

    # ---- 恢复：写 0 + 广播 Environment ----
    Write-Output '--- 恢复：写 0 + 广播 Environment ---'
    Set-HideIcons 0
    $b3 = [DI2]::Broadcast('Environment')
    $r3 = Wait-Icons $true 5000
    Write-Output ("    广播={0}；DefView 可见={1}（{2} ms）；图标数={3}" -f $b3, $r3.Ok, $r3.Ms, $r3.State.Icons)

    # ---- 补一刀 ImmersiveShell ----
    Write-Output '--- 补广播 ImmersiveShell ---'
    $b4 = [DI2]::Broadcast('ImmersiveShell')
    $r4 = Wait-Icons $true 5000
    Write-Output ("    广播={0}；DefView 可见={1}（{2} ms）；图标数={3}" -f $b4, $r4.Ok, $r4.Ms, $r4.State.Icons)
}
finally {
    Write-Output '===== finally：还原注册表基线 ====='
    if ($origPresent) { Set-HideIcons $orig } else { Remove-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue }
    [void][DI2]::Broadcast('ImmersiveShell')
    $r = Wait-Icons $true 5000
    Write-Output ("最终 HideIcons = {0}（基线 {1}）；DefView 可见 = {2}" -f `
        $(if ($null -eq (Get-RegHideIcons)) { 'ABSENT' } else { Get-RegHideIcons }), `
        $(if ($origPresent) { "$orig" } else { 'ABSENT' }), $r.Ok)
}
Write-Output '===== 测试2（权威判据）结束 ====='
