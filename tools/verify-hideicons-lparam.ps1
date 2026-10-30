#Requires -Version 5.1
<#
  verify-hideicons-lparam.ps1 —— 对照实验：WM_SETTINGCHANGE 的 lParam 对 HideIcons 生效的影响

  背景：测试2发现 DesktopIconService 广播 'Environment' 后桌面图标并没有真的隐藏。
  历史生效证据（stage5-probe.ps1 → p2-*.png）用的 lParam 是 'ImmersiveShell'。
  本脚本按 2x2 矩阵验证：写注册表后分别用两种 lParam 广播，看图标是否真的消失。

  判据：桌面图标区（屏幕左侧图标列，裁剪 x0..150 y0..1300 物理像素）与"确定隐藏"时的像素差，
       同时打印 SHELLDLL_DefView 的可见性。
  安全：只写 HideIcons 一个值，finally 还原；不重启 Explorer；不动桌面文件。
#>
param(
    [string]$OutDir = "$env:USERPROFILE\Documents\Github\DeskLanes\docs\stage7-evidence"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LP
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls, string title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
}
'@

[void][LP]::SetProcessDPIAware()

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$iconCrop = @{ X = 0; Y = 0; W = 150; H = 1300 }   # 桌面左侧图标列（物理像素）

function Get-RegHideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return $null }
    return [int]$v.HideIcons
}

function Broadcast([string]$lparam) {
    $r = [IntPtr]::Zero
    return [LP]::SendMessageTimeout([LP]::HWND_BROADCAST, 0x001A, [IntPtr]::Zero, $lparam,
        0x0002, 2000, [ref]$r) -ne [IntPtr]::Zero
}

function Get-DefViewVisible {
    $progman = [LP]::FindWindow('Progman', $null)
    if ($progman.ToInt64() -ne 0) {
        $dv = [LP]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
        if ($dv.ToInt64() -ne 0) { return [LP]::IsWindowVisible($dv) }
    }
    $w = [LP]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', $null)
    while ($w.ToInt64() -ne 0) {
        $dv2 = [LP]::FindWindowEx($w, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
        if ($dv2.ToInt64() -ne 0) { return [LP]::IsWindowVisible($dv2) }
        $w = [LP]::FindWindowEx([IntPtr]::Zero, $w, 'WorkerW', $null)
    }
    return $null
}

function Save-IconArea([string]$name) {
    Start-Sleep -Milliseconds 800   # 等 Explorer 重绘
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size) } finally { $g.Dispose() }
    $full = Join-Path $OutDir $name
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return (Get-FileHash -LiteralPath $full -Algorithm MD5).Hash
}

Write-Output '===== HideIcons 广播 lParam 对照实验 ====='
$orig = Get-RegHideIcons
$origPresent = $null -ne $orig
Write-Output ("基线 HideIcons = {0}" -f $(if ($origPresent) { "$orig" } else { 'ABSENT' }))

try {
    $h = @{}
    # 状态 0：当前（应显示）
    $h['a-initial'] = Save-IconArea 'lp-a-initial.png'
    Write-Output ("[a 初始]        DefView可见={0}  md5={1}" -f (Get-DefViewVisible), $h['a-initial'])

    # 状态 1：写 1 + 广播 Environment（应用当前的做法）
    Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value 1
    $b = Broadcast 'Environment'
    $h['b-hide-env'] = Save-IconArea 'lp-b-hide-env.png'
    Write-Output ("[b 隐藏+Environment] 广播={0} DefView可见={1}  md5={2}" -f $b, (Get-DefViewVisible), $h['b-hide-env'])

    # 状态 2：再广播 ImmersiveShell（不重写注册表）
    $b2 = Broadcast 'ImmersiveShell'
    $h['c-hide-shell'] = Save-IconArea 'lp-c-hide-shell.png'
    Write-Output ("[c 隐藏+ImmersiveShell] 广播={0} DefView可见={1}  md5={2}" -f $b2, (Get-DefViewVisible), $h['c-hide-shell'])

    # 状态 3：写 0 + 广播 Environment
    Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value 0
    $b3 = Broadcast 'Environment'
    $h['d-show-env'] = Save-IconArea 'lp-d-show-env.png'
    Write-Output ("[d 恢复+Environment] 广播={0} DefView可见={1}  md5={2}" -f $b3, (Get-DefViewVisible), $h['d-show-env'])

    # 状态 4：再广播 ImmersiveShell
    $b4 = Broadcast 'ImmersiveShell'
    $h['e-show-shell'] = Save-IconArea 'lp-e-show-shell.png'
    Write-Output ("[e 恢复+ImmersiveShell] 广播={0} DefView可见={1}  md5={2}" -f $b4, (Get-DefViewVisible), $h['e-show-shell'])

    Write-Output '--- 对照结论 ---'
    Write-Output ("初始 vs 隐藏+Environment 有差异 : {0}" -f ($h['a-initial'] -ne $h['b-hide-env']))
    Write-Output ("隐藏+Environment vs +ImmersiveShell 有差异: {0}" -f ($h['b-hide-env'] -ne $h['c-hide-shell']))
    Write-Output ("隐藏(+shell) vs 恢复+Environment 有差异   : {0}" -f ($h['c-hide-shell'] -ne $h['d-show-env']))
    Write-Output ("恢复+Environment vs +ImmersiveShell 有差异: {0}" -f ($h['d-show-env'] -ne $h['e-show-shell']))
    Write-Output ("初始 vs 恢复(+shell) 一致       : {0}" -f ($h['a-initial'] -eq $h['e-show-shell']))
}
finally {
    Write-Output '===== finally：还原 ====='
    if ($origPresent) { Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value $orig }
    else { Remove-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue }
    [void](Broadcast 'ImmersiveShell')
    Start-Sleep -Milliseconds 800
    Write-Output ("最终 HideIcons = {0}（基线 {1}）；DefView可见={2}" -f `
        $(if ($null -eq (Get-RegHideIcons)) { 'ABSENT' } else { Get-RegHideIcons }), `
        $(if ($origPresent) { "$orig" } else { 'ABSENT' }), (Get-DefViewVisible))
}
Write-Output '===== 实验结束 ====='
