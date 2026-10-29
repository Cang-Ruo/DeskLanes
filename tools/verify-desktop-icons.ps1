#Requires -Version 5.1
<#
  verify-desktop-icons.ps1 —— 阶段 7 授权测试第 2 项：桌面图标可见性复测

  目的：
    1. 用与 DesktopIconService 完全相同的机制（写 HKCU HideIcons + WM_SETTINGCHANGE 广播）
       做两轮 隐藏→恢复，每轮以「SHELLDLL_DefView 可见性」为准判定真实显示状态，
       截图并做 MD5 比对，复核历史疑点 gt4-02 == gt4-03（重复切换是否有视觉变化）
    2. 桌面文件指纹前后比对，证明没有动任何桌面文件

  安全：只写 Explorer\Advanced\HideIcons 这一个注册表值（与系统右键菜单同一行为），
       finally 保证还原；不杀/不重启 Explorer；不动桌面文件；不移动鼠标。
#>
param(
    [string]$OutDir = 'C:\Users\TTjustTT\Documents\Github\DeskLanes\docs\stage7-evidence'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct DI_RECT { public int Left, Top, Right, Bottom; }

public static class DI
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

[void][DI]::SetProcessDPIAware()

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

function Get-RegHideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return $null }
    return [int]$v.HideIcons
}

function Set-HideIcons([int]$val) {
    Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value $val
}

function Broadcast {
    $r = [IntPtr]::Zero
    # 与 DesktopIconService.NativeMethods.BroadcastSettingChange 相同的参数
    return [DI]::SendMessageTimeout([DI]::HWND_BROADCAST, [DI]::WM_SETTINGCHANGE, [IntPtr]::Zero,
        'Environment', [DI]::SMTO_ABORTIFHUNG, 2000, [ref]$r) -ne [IntPtr]::Zero
}

function Get-DefViews {
    $rows = @()
    $progman = [DI]::FindWindow('Progman', $null)
    if ($progman.ToInt64() -ne 0) {
        $dv = [DI]::FindWindowEx($progman, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
        if ($dv.ToInt64() -ne 0) {
            $rows += @{ Host = 'Progman'; Hwnd = $dv; Visible = [DI]::IsWindowVisible($dv) }
        }
    }
    $w = [DI]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', $null)
    while ($w.ToInt64() -ne 0) {
        $dv2 = [DI]::FindWindowEx($w, [IntPtr]::Zero, 'SHELLDLL_DefView', $null)
        if ($dv2.ToInt64() -ne 0) {
            $rows += @{ Host = ('WorkerW:0x{0:X}' -f $w.ToInt64()); Hwnd = $dv2; Visible = [DI]::IsWindowVisible($dv2) }
        }
        $w = [DI]::FindWindowEx([IntPtr]::Zero, $w, 'WorkerW', $null)
    }
    return $rows
}

function Test-AllVisible([bool]$want) {
    $views = Get-DefViews
    if ($views.Count -eq 0) { return $false }
    foreach ($v in $views) { if ($v.Visible -ne $want) { return $false } }
    return $true
}

function Wait-State([bool]$wantVisible, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-AllVisible $wantVisible) { return @{ Ok = $true; Ms = $sw.ElapsedMilliseconds } }
        Start-Sleep -Milliseconds 150
    }
    return @{ Ok = $false; Ms = $sw.ElapsedMilliseconds }
}

function Save-Shot([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size) } finally { $g.Dispose() }
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return (Get-FileHash -LiteralPath $path -Algorithm MD5).Hash
}

function Get-DesktopFingerprint {
    $dirs = @()
    $d = [Environment]::GetFolderPath('Desktop'); if ($d) { $dirs += $d }
    $p = [Environment]::GetFolderPath('CommonDesktopDirectory'); if ($p) { $dirs += $p }
    $total = 0; $max = [DateTime]::MinValue
    foreach ($dir in $dirs) {
        $items = Get-ChildItem -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        $total += $items.Count
        foreach ($it in $items) { if ($it.LastWriteTimeUtc -gt $max) { $max = $it.LastWriteTimeUtc } }
    }
    return ("count={0} maxWriteUtc={1:o}" -f $total, $max)
}

function Report-State([string]$label) {
    $views = Get-DefViews
    $reg = Get-RegHideIcons
    $vis = (@($views | Where-Object { $_.Visible }).Count)
    Write-Output ("[{0}] HideIcons={1}  DefView 可见 {2}/{3}" -f $label, $(if ($null -eq $reg) { 'ABSENT' } else { $reg }), $vis, $views.Count)
    return $views.Count
}

Write-Output '===== 阶段7 授权测试2：桌面图标可见性复测 ====='
$orig = Get-RegHideIcons
$origPresent = $null -ne $orig
Write-Output ("基线 HideIcons = {0}" -f $(if ($origPresent) { "$orig" } else { 'ABSENT（默认显示）' }))

$fpBefore = Get-DesktopFingerprint
Write-Output ("桌面文件指纹（前）: {0}" -f $fpBefore)

$md5 = @{}
try {
    # ---- 基线 ----
    [void](Report-State '00 基线')
    $md5['01-baseline'] = Save-Shot 'di-01-baseline.png'

    # ---- 第 1 轮：隐藏 ----
    Write-Output '--- 第 1 轮：隐藏（写注册表 + 广播） ---'
    Set-HideIcons 1
    $ok = Broadcast
    $r = Wait-State $false 4000
    Write-Output ("    广播返回={0}；DefView 全部隐藏={1}（{2} ms）" -f $ok, $r.Ok, $r.Ms)
    [void](Report-State '01 隐藏后')
    $md5['02-hidden-r1'] = Save-Shot 'di-02-hidden-round1.png'

    # ---- 第 1 轮：恢复 ----
    Write-Output '--- 第 1 轮：恢复 ---'
    Set-HideIcons 0
    $ok = Broadcast
    $r = Wait-State $true 5000
    Write-Output ("    广播返回={0}；DefView 全部可见={1}（{2} ms）" -f $ok, $r.Ok, $r.Ms)
    [void](Report-State '02 恢复后')
    $md5['03-restored-r1'] = Save-Shot 'di-03-restored-round1.png'

    # ---- 第 2 轮：再隐藏 ----
    Write-Output '--- 第 2 轮：再隐藏（复核重复切换疑点） ---'
    Set-HideIcons 1
    $ok = Broadcast
    $r = Wait-State $false 4000
    Write-Output ("    广播返回={0}；DefView 全部隐藏={1}（{2} ms）" -f $ok, $r.Ok, $r.Ms)
    [void](Report-State '03 再隐藏后')
    $md5['04-hidden-r2'] = Save-Shot 'di-04-hidden-round2.png'

    # ---- 第 2 轮：再恢复 ----
    Write-Output '--- 第 2 轮：再恢复 ---'
    Set-HideIcons 0
    $ok = Broadcast
    $r = Wait-State $true 5000
    Write-Output ("    广播返回={0}；DefView 全部可见={1}（{2} ms）" -f $ok, $r.Ok, $r.Ms)
    [void](Report-State '04 再恢复后')
    $md5['05-restored-r2'] = Save-Shot 'di-05-restored-round2.png'

    # ---- 汇总 ----
    Write-Output '--- MD5 汇总 ---'
    foreach ($k in @('01-baseline', '02-hidden-r1', '03-restored-r1', '04-hidden-r2', '05-restored-r2')) {
        Write-Output ("    {0,-16} {1}" -f $k, $md5[$k])
    }
    Write-Output ("判定 基线 vs 隐藏(轮1) 不同   : {0}" -f ($md5['01-baseline'] -ne $md5['02-hidden-r1']))
    Write-Output ("判定 隐藏(轮1) vs 恢复(轮1) 不同: {0}" -f ($md5['02-hidden-r1'] -ne $md5['03-restored-r1']))
    Write-Output ("判定 恢复(轮1) == 基线        : {0}" -f ($md5['03-restored-r1'] -eq $md5['01-baseline']))
    Write-Output ("判定 隐藏(轮2) == 隐藏(轮1)   : {0}" -f ($md5['04-hidden-r2'] -eq $md5['02-hidden-r1']))
    Write-Output ("判定 恢复(轮2) == 恢复(轮1)   : {0}" -f ($md5['05-restored-r2'] -eq $md5['03-restored-r1']))
    Write-Output ("判定 恢复(轮2) == 基线        : {0}" -f ($md5['05-restored-r2'] -eq $md5['01-baseline']))
}
finally {
    Write-Output '===== finally：还原注册表基线 ====='
    if ($origPresent) { Set-HideIcons $orig } else { Remove-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue }
    [void](Broadcast)
    $r = Wait-State $true 5000
    $finalReg = Get-RegHideIcons
    Write-Output ("最终 HideIcons = {0}（基线 {1}）；DefView 全部可见 = {2}" -f `
        $(if ($null -eq $finalReg) { 'ABSENT' } else { $finalReg }), `
        $(if ($origPresent) { "$orig" } else { 'ABSENT' }), $r.Ok)
    $fpAfter = Get-DesktopFingerprint
    Write-Output ("桌面文件指纹（后）: {0}" -f $fpAfter)
    Write-Output ("桌面文件指纹一致（没动任何桌面文件）: {0}" -f ($fpAfter -eq $fpBefore))
}
Write-Output '===== 测试2 结束 ====='
