#Requires -Version 5.1
<#
  verify-published-exe.ps1 —— 阶段 7 授权测试第 1 项：单文件产物实机验证

  验证内容：
    A. 托盘图标来源：从发布产物 exe 里 ExtractIconEx 取图标，与 Assets/app.ico、
       系统默认 IDI_APPLICATION 逐像素比对（MD5），证明托盘图标不再是通用图标兜底
    B. 实机启动：进程存活 / 主窗口句柄 / 窗口标题 / 窗口可见性
    C. 截图证据：主屏全屏 + 任务栏托盘区裁剪

  安全：只启动与结束 DeskLanes 自己，不动桌面文件、不重启 Explorer、不改系统设置。
#>
param(
    [string]$ExePath = 'C:\Users\TTjustTT\Documents\Github\DeskLanes\publish\win-x64\DeskLanes.exe',
    [string]$IcoPath = 'C:\Users\TTjustTT\Documents\Github\DeskLanes\src\DeskLanes.App\Assets\app.ico',
    [string]$OutDir  = 'C:\Users\TTjustTT\Documents\Github\DeskLanes\docs\stage7-evidence'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing


Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct VRRect { public int Left, Top, Right, Bottom; }

public static class V {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls, string title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr h, out VRRect r);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    public const uint DI_NORMAL = 0x0003;
    public static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);
}
'@
# 关键：让本进程 DPI-aware，否则 Screen.Bounds / GetWindowRect / CopyFromScreen
# 会分属"逻辑像素"与"物理像素"两套坐标系，托盘裁剪会截到错误的屏幕区域
[void][V]::SetProcessDPIAware()

function Save-IconPng([IntPtr]$hIcon, [int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # 铺一层洋红底：让透明区域可见，且保证两次渲染的背景完全一致（可逐像素比对）
    $g.Clear([System.Drawing.Color]::Magenta)
    $hdc = $g.GetHdc()
    try {
        [void][V]::DrawIconEx($hdc, 0, 0, $hIcon, $size, $size, 0, [IntPtr]::Zero, [V]::DI_NORMAL)
    } finally {
        $g.ReleaseHdc($hdc)
        $g.Dispose()
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return (Get-FileHash -LiteralPath $path -Algorithm MD5).Hash
}

function Write-Step($msg) { Write-Output "---- $msg" }

Write-Output '===== 阶段7 授权测试1：单文件产物实机验证 ====='
Write-Output ("exe = {0}" -f $ExePath)
Write-Output ("size = {0} bytes" -f (Get-Item -LiteralPath $ExePath).Length)

Write-Step 'A. 托盘图标来源比对'

$large = [IntPtr]::Zero
$small = [IntPtr]::Zero
$n = [V]::ExtractIconEx($ExePath, 0, [ref]$large, [ref]$small, 1)
Write-Output ("ExtractIconEx(exe) 返回 {0} 个图标；large=0x{1:X} small=0x{2:X}" -f $n, $large.ToInt64(), $small.ToInt64())

# 托盘用的是 small（见 TrayIconService.LoadIcon：优先返回 small），因此以 16x16 为准做比对；
# 同时按各自原生尺寸渲染，避免"把 16x16 拉到 32x32"造成的假性不一致。
$exeSmallHash = $null
$exeLargeHash = $null
if ($n -gt 0) {
    if ($small.ToInt64() -ne 0) { $exeSmallHash = Save-IconPng $small 16 (Join-Path $OutDir 'icon-from-exe-16.png') }
    if ($large.ToInt64() -ne 0) { $exeLargeHash = Save-IconPng $large 32 (Join-Path $OutDir 'icon-from-exe-32.png') }
}

$ico16 = New-Object System.Drawing.Icon($IcoPath, 16, 16)
$src16Hash = Save-IconPng $ico16.Handle 16 (Join-Path $OutDir 'icon-from-source-ico-16.png')
$ico16.Dispose()

$ico32 = New-Object System.Drawing.Icon($IcoPath, 32, 32)
$src32Hash = Save-IconPng $ico32.Handle 32 (Join-Path $OutDir 'icon-from-source-ico-32.png')
$ico32.Dispose()

$def = [V]::LoadIcon([IntPtr]::Zero, [V]::IDI_APPLICATION)
$def16Hash = Save-IconPng $def 16 (Join-Path $OutDir 'icon-default-application-16.png')
$def32Hash = Save-IconPng $def 32 (Join-Path $OutDir 'icon-default-application-32.png')

Write-Output ("16x16  发布产物exe : {0}" -f $exeSmallHash)
Write-Output ("16x16  Assets/ico  : {0}" -f $src16Hash)
Write-Output ("16x16  系统默认图标: {0}" -f $def16Hash)
Write-Output ("32x32  发布产物exe : {0}" -f $exeLargeHash)
Write-Output ("32x32  Assets/ico  : {0}" -f $src32Hash)
Write-Output ("32x32  系统默认图标: {0}" -f $def32Hash)
if ($exeSmallHash) {
    Write-Output ("[托盘图标] exe small == 源ico 16x16 : {0}" -f ($exeSmallHash -eq $src16Hash))
    Write-Output ("[托盘图标] exe small != 系统默认     : {0}" -f ($exeSmallHash -ne $def16Hash))
} else {
    Write-Output '结论  ExtractIconEx 取图标失败（托盘会走 IDI_APPLICATION 兜底）—— 需要排查'
}
if ($large.ToInt64() -ne 0) { [void][V]::DestroyIcon($large) }
if ($small.ToInt64() -ne 0) { [void][V]::DestroyIcon($small) }

Write-Step 'B. 实机启动'

Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$p = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 7
# Start-Process 拿到的 MainWindowHandle 可能是启动瞬间的旧值（常为 0），先刷新再读
$p.Refresh()

Write-Output ("HasExited          = {0}" -f $p.HasExited)
Write-Output ("MainWindowHandle   = 0x{0:X}" -f $p.MainWindowHandle.ToInt64())

$sb = New-Object System.Text.StringBuilder 256
if ($p.MainWindowHandle.ToInt64() -ne 0) {
    [void][V]::GetWindowText($p.MainWindowHandle, $sb, 256)
}
Write-Output ("MainWindowTitle    = [{0}]" -f $sb.ToString())
Write-Output ("MainWindowVisible  = {0}" -f [V]::IsWindowVisible($p.MainWindowHandle))

# 枚举属于该进程的所有顶层可见窗口
$owned = New-Object System.Collections.ArrayList
$cb = [V+EnumWindowsProc]{
    param([IntPtr]$h, [IntPtr]$l)
    $pid2 = 0
    [void][V]::GetWindowThreadProcessId($h, [ref]$pid2)
    if ($pid2 -eq $script:targetPid) {
        $t = New-Object System.Text.StringBuilder 256
        [void][V]::GetWindowText($h, $t, 256)
        [void]$script:owned.Add(('0x{0:X}' -f $h.ToInt64()) + ' visible=' + [V]::IsWindowVisible($h) + ' title=[' + $t.ToString() + ']')
    }
    return $true
}
$script:targetPid = $p.Id
$script:owned = $owned
[void][V]::EnumWindows($cb, [IntPtr]::Zero)
Write-Output ("该进程的顶层窗口数 = {0}" -f $owned.Count)
foreach ($w in $owned) { Write-Output ("    {0}" -f $w) }

Write-Step 'C. 截图证据'

# 主屏全屏
$pb = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($pb.Width, $pb.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try { $g.CopyFromScreen($pb.Location, [System.Drawing.Point]::Empty, $pb.Size) } finally { $g.Dispose() }
$bmp.Save((Join-Path $OutDir 'published-full-primary.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ('已保存 主屏全屏: published-full-primary.png')

# 任务栏 + 托盘区裁剪
$tray = [V]::FindWindow('Shell_TrayWnd', $null)
$rect = New-Object VRRect
if ([V]::GetWindowRect($tray, [ref]$rect)) {
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $tb = New-Object System.Drawing.Bitmap($w, $h)
    $tg = [System.Drawing.Graphics]::FromImage($tb)
    try {
        $tg.CopyFromScreen((New-Object System.Drawing.Point($rect.Left, $rect.Top)),
                           [System.Drawing.Point]::Empty,
                           (New-Object System.Drawing.Size($w, $h)))
    } finally { $tg.Dispose() }
    $tb.Save((Join-Path $OutDir 'published-taskbar-crop.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $tb.Dispose()
    Write-Output ("已保存 任务栏裁剪 ({0}x{1}): published-taskbar-crop.png" -f $w, $h)

    # 只截托盘通知区（TrayNotifyWnd），图标更小更清楚
    $tn = [V]::FindWindowEx($tray, [IntPtr]::Zero, 'TrayNotifyWnd', $null)
    if ($tn.ToInt64() -ne 0) {
        $r2 = New-Object VRRect
        if ([V]::GetWindowRect($tn, [ref]$r2)) {
            $w2 = $r2.Right - $r2.Left; $h2 = $r2.Bottom - $r2.Top
            if ($w2 -gt 0 -and $h2 -gt 0) {
                $nb = New-Object System.Drawing.Bitmap($w2, $h2)
                $ng = [System.Drawing.Graphics]::FromImage($nb)
                try {
                    $ng.CopyFromScreen((New-Object System.Drawing.Point($r2.Left, $r2.Top)),
                                       [System.Drawing.Point]::Empty,
                                       (New-Object System.Drawing.Size($w2, $h2)))
                } finally { $ng.Dispose() }
                $nb.Save((Join-Path $OutDir 'published-tray-crop.png'), [System.Drawing.Imaging.ImageFormat]::Png)
                $nb.Dispose()
                Write-Output ("已保存 托盘区裁剪 ({0}x{1}): published-tray-crop.png" -f $w2, $h2)
            }
        }
    } else {
        Write-Output '未找到 TrayNotifyWnd（托盘可能藏在溢出区）'
    }
} else {
    Write-Output '未找到 Shell_TrayWnd'
}

Write-Step 'D. 结束进程'
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep -Milliseconds 600 }
Write-Output ("已结束 DeskLanes (pid {0}); 残留进程数 = {1}" -f $p.Id, @(Get-Process -Name DeskLanes -ErrorAction SilentlyContinue).Count)
Write-Output '===== 测试1 结束 ====='
