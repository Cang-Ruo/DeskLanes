#Requires -Version 5.1
<#
  verify-hideicons-final.ps1 —— 桌面图标隐藏的最终判定实验

  背景：
    - DefView/SysListView32 读数在图标隐藏时仍报"可见/76"（不可靠）
    - 全屏截图 MD5 一直不同（疑似动态壁纸 + 屏幕内容变化，噪声太大）
  本脚本改用「图标区域像素差幅度」做判定：
    - 与基线比，平均色差很小  → 图标还在（壁纸噪声级别）
    - 与基线比，平均色差很大  → 图标没了

  步骤矩阵：
    0. 稳定性对照：同状态间隔 2s 拍两次，量噪声底噪
    1. 写 HideIcons=1 + 广播 Environment（应用当前实现）
    2. 补广播 ImmersiveShell
    3. WM_COMMAND 0x7402 切换（OS 菜单同款）
    4. 再发一次 0x7402（恢复）+ 还原注册表
#>
param(
    [string]$OutDir = 'C:\Users\TTjustTT\Documents\Github\DeskSpace\docs\stage7-evidence'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FN
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

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

    public static bool Broadcast(string lparam)
    {
        IntPtr r;
        return SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, lparam, 0x0002, 2000, out r) != IntPtr.Zero;
    }
}
'@

[void][FN]::SetProcessDPIAware()

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

# 图标区域：主屏左上 900x1300 物理像素（覆盖桌面图标网格，避开任务栏和居中的窗口）
$cropW = 900; $cropH = 1300

function Get-CropBitmap {
    $bmp = New-Object System.Drawing.Bitmap($cropW, $cropH)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen((New-Object System.Drawing.Point(0, 0)),
                          [System.Drawing.Point]::Empty,
                          (New-Object System.Drawing.Size($cropW, $cropH)))
    } finally { $g.Dispose() }
    return $bmp
}

# 计算两图采样像素的平均绝对差（0-255）
function Get-Diff([System.Drawing.Bitmap]$a, [System.Drawing.Bitmap]$b) {
    $total = 0.0; $n = 0
    for ($y = 0; $y -lt $cropH; $y += 6) {
        for ($x = 0; $x -lt $cropW; $x += 6) {
            $pa = $a.GetPixel($x, $y); $pb = $b.GetPixel($x, $y)
            $total += ([Math]::Abs($pa.R - $pb.R) + [Math]::Abs($pa.G - $pb.G) + [Math]::Abs($pa.B - $pb.B)) / 3.0
            $n++
        }
    }
    if ($n -eq 0) { return -1 }
    return [Math]::Round($total / $n, 2)
}

function Get-HideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return 'ABSENT' }
    return [int]$v.HideIcons
}

function Get-DefViewHwnd {
    $progman = [FN]::FindTopWindowByClass('Progman')
    if ($progman.ToInt64() -eq 0) { return [IntPtr]::Zero }
    $dv = [FN]::FindDefView($progman)
    return $dv
}

function Send-Toggle {
    $dv = Get-DefViewHwnd
    if ($dv.ToInt64() -ne 0) { [void][FN]::SendMessage($dv, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero) }
    return $dv.ToInt64()
}

Write-Output '===== HideIcons 最终判定实验（像素差幅度法） ====='
$orig = Get-HideIcons
Write-Output ("基线 HideIcons = {0}" -f $orig)

$base = Get-CropBitmap
$base.Save((Join-Path $OutDir 'fn-0-baseline.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# 0. 噪声底噪
Start-Sleep -Seconds 2
$noiseB = Get-CropBitmap
$noise = Get-Diff $base $noiseB
$noiseB.Dispose()
Write-Output ("[0 噪声底噪] 同状态 2s 间隔平均色差 = {0}（这是「没变化」的参照）" -f $noise)

function Step([string]$label, [string]$file) {
    Start-Sleep -Seconds 2   # 等动画/重绘结束
    $b = Get-CropBitmap
    $b.Save((Join-Path $OutDir $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $d = Get-Diff $base $b
    $b.Dispose()
    Write-Host ("[{0}] HideIcons={1}  与基线平均色差 = {2}" -f $label, (Get-HideIcons), $d)
    return $d
}

try {
    Write-Output '--- 1. 写 1 + 广播 Environment（应用当前实现） ---'
    Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value 1
    $b1 = [FN]::Broadcast('Environment')
    $d1 = Step '1 隐藏+Environment' 'fn-1-hide-env.png'
    Write-Output ("    广播返回={0}" -f $b1)

    Write-Output '--- 2. 补广播 ImmersiveShell ---'
    $b2 = [FN]::Broadcast('ImmersiveShell')
    $d2 = Step '2 隐藏+ImmersiveShell' 'fn-2-hide-shell.png'
    Write-Output ("    广播返回={0}" -f $b2)

    Write-Output '--- 3. WM_COMMAND 0x7402（OS 菜单同款；当前注册表已是 1，这次切换会变成 0=显示） ---'
    $dvSent = Send-Toggle
    Write-Output ("    发送目标 DefView = 0x{0:X}" -f $dvSent)
    $d3 = Step '3 切换一次后' 'fn-3-toggle1.png'

    Write-Output '--- 4. 再发一次 0x7402（回到隐藏态？） ---'
    $dvSent2 = Send-Toggle
    $d4 = Step '4 切换两次后' 'fn-4-toggle2.png'
}
finally {
    Write-Output '===== finally：还原 ====='
    if ($orig -eq 'ABSENT') { Remove-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $advKey -Name HideIcons -Type DWord -Value $orig }
    [void][FN]::Broadcast('ImmersiveShell')
    Start-Sleep -Seconds 2
    $fin = Get-CropBitmap
    $dfin = Get-Diff $base $fin
    $fin.Dispose()
    Write-Output ("[终态] HideIcons={0}  与基线平均色差 = {1}（应接近底噪 {2}）" -f (Get-HideIcons), $dfin, $noise)
}
Write-Output '===== 实验结束 ====='
