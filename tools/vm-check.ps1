#Requires -Version 5.1
<#
  vm-check.ps1 —— 另一台电脑 / 虚拟机上的 DeskSpace 体检脚本（独立运行，不依赖仓库）

  用途：回答两个问题
    1. 这台机器能不能跑 DeskSpace（有没有 .NET 10 桌面运行时、是 x64 还是 ARM）
    2. 实际启动后窗口有没有出来、有没有跑出屏幕（不同 DPI / 单显示器下的定位）

  用法（把 vm-check.ps1 和 DeskSpace.exe 放在同一目录）：
    powershell -NoProfile -ExecutionPolicy Bypass -File vm-check.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File vm-check.ps1 -Exe D:\tmp\DeskSpace.exe
    powershell -NoProfile -ExecutionPolicy Bypass -File vm-check.ps1 -KeepRunning   # 跑完不关程序

  产出：
    屏幕上的实时输出 + 同目录 vm-check-report.txt（可直接发回来）
    启动失败时还会自动截一张全屏 vm-check-fail.png（用来确认报错提示是否清楚）
#>
param(
    [string]$Exe = '',
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$reportPath = Join-Path $scriptDir 'vm-check-report.txt'
$shotPath   = Join-Path $scriptDir 'vm-check-fail.png'
$lines = New-Object System.Collections.ArrayList

function Say($m) {
    Write-Host $m
    [void]$lines.Add($m)
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Vm {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@

# DPI 感知要在拿屏幕尺寸之前设，否则缩放不是 100% 时坐标会错位
[void][Vm]::SetProcessDPIAware()
$sysDpi = 96
try { $sysDpi = [Vm]::GetDpiForSystem() } catch { $sysDpi = 96 }

function Take-Screenshot([string]$path) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Test-DesktopRuntime {
    $roots = @()
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App') }
    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) { $roots += (Join-Path $pf86 'dotnet\shared\Microsoft.WindowsDesktop.App') }
    if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA 'Programs\dotnet\shared\Microsoft.WindowsDesktop.App') }
    if ($env:USERPROFILE) { $roots += (Join-Path $env:USERPROFILE '.dotnet\shared\Microsoft.WindowsDesktop.App') }
    $found = @()
    foreach ($r in $roots) {
        if (Test-Path -LiteralPath $r) {
            $v = Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '10.*' }
            if ($v) { $found += ($v | ForEach-Object { $_.Name }) }
        }
    }
    return $found
}

Say '==================== DeskSpace 虚拟机 / 第二台机器 体检 ===================='
Say ('体检时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Say ''

# ---------- 1. 机器信息 ----------
Say '--- 1. 机器信息 ---'
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) {
    Say ('系统      : ' + $os.Caption + ' (Build ' + $os.BuildNumber + ')')
    Say ('版本      : ' + $os.Version)
}
Say ('架构      : ' + $env:PROCESSOR_ARCHITECTURE + '（x64 才能跑当前发布的版本；ARM64 靠模拟也能跑但会慢）')
Say ('系统 DPI  : ' + $sysDpi + '（' + [math]::Round($sysDpi / 96 * 100) + '% 缩放）')

try {
    Add-Type -AssemblyName System.Windows.Forms
    $screens = [System.Windows.Forms.Screen]::AllScreens
    Say ('显示器数  : ' + @($screens).Count)
    $i = 1
    foreach ($s in $screens) {
        $b = $s.Bounds
        $w = $s.WorkingArea
        Say ('  屏 ' + $i + ': 主屏=' + $s.Primary + '  分辨率=' + $b.Width + 'x' + $b.Height +
             '  位置=(' + $b.X + ',' + $b.Y + ')' +
             '  工作区=' + $w.Width + 'x' + $w.Height + ' @(' + $w.X + ',' + $w.Y + ')')
        $i = $i + 1
    }
} catch {
    Say '显示器数  : 读取失败（' + $_.Exception.Message + '）'
}

# ---------- 2. 运行时 ----------
Say ''
Say '--- 2. .NET 10 桌面运行时（便携版的硬依赖） ---'
$rt = Test-DesktopRuntime
if ($rt) {
    Say ('已安装    : Microsoft.WindowsDesktop.App ' + ($rt -join ', ') + '  ✓')
    Say '结论      : 便携版和自包含版都能跑'
} else {
    Say '未发现    : Microsoft.WindowsDesktop.App 10.x'
    Say '结论      : 便携版打不开；自包含版不受影响，应当能直接跑'
    Say '           需要便携版的话先装：https://dotnet.microsoft.com/download/dotnet/10.0 → 桌面运行时'
}

# ---------- 3. 定位 exe ----------
Say ''
Say '--- 3. DeskSpace.exe ---'
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $cand = Join-Path $scriptDir 'DeskSpace.exe'
    if (Test-Path -LiteralPath $cand) { $Exe = $cand }
}
if ([string]::IsNullOrWhiteSpace($Exe) -or -not (Test-Path -LiteralPath $Exe)) {
    Say '没有找到 DeskSpace.exe。'
    Say '把 DeskSpace.exe 放到 vm-check.ps1 同目录，或用 -Exe 指定路径后重跑。'
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    Say ('报告已写入: ' + $reportPath)
    exit 0
}
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$size = (Get-Item -LiteralPath $Exe).Length
$edition = if ($size -gt 20MB) { 'selfcontained（自包含版，自带运行时）' } else { 'portable（便携版，需要 .NET 10 桌面运行时）' }
Say ('路径      : ' + $Exe)
Say ('大小      : ' + $size + ' 字节 → ' + $edition)

# ---------- 4. 启动验证 ----------
Say ''
Say '--- 4. 启动验证 ---'
$extractRoot = Join-Path $env:TEMP '.net\DeskSpace'
if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue }

$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 15
$alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue

if (-not $alive) {
    Say '结果      : ✗ 进程在 15 秒内退出了（启动失败）'
    if ($edition -like 'portable*' -and -not $rt) {
        Say '原因判断  : 这台机器没有 .NET 10 桌面运行时，便携版打不开 —— 这是预期内的失败'
    }
    $ok = Take-Screenshot $shotPath
    if ($ok) { Say ('已截全屏  : ' + $shotPath + '  ← 请把这张图一起发回来，确认报错提示是否看得懂') }
    else { Say '截图失败  : 无法自动截图，请把屏幕上弹出的提示文字抄回来' }
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    Say ('报告已写入: ' + $reportPath)
    exit 2
}

Say '结果      : ✓ 进程仍在运行（15 秒后）'

$script:titles = @()
$script:rects = @()
$cb = [Vm+EnumProc]{
    param($h, $l)
    $p2 = 0
    $null = [Vm]::GetWindowThreadProcessId($h, [ref]$p2)
    if ($p2 -eq $script:targetPid -and [Vm]::IsWindowVisible($h)) {
        $len = [Vm]::GetWindowTextLength($h)
        $sb = New-Object System.Text.StringBuilder ($len + 1)
        $null = [Vm]::GetWindowText($h, $sb, $sb.Capacity)
        $r = New-Object Vm+RECT
        $null = [Vm]::GetWindowRect($h, [ref]$r)
        $script:titles += $sb.ToString()
        $script:rects += $r
    }
    return $true
}
$script:targetPid = [uint32]$proc.Id
$null = [Vm]::EnumWindows($cb, [IntPtr]::Zero)

Say ('可见窗口  : ' + @($script:titles).Count + ' 个')
$idx = 0
foreach ($t in $script:titles) {
    $r = $script:rects[$idx]
    Say ('   「' + $t + '」 位置=(' + $r.L + ',' + $r.T + ') 大小=' + ($r.R - $r.L) + 'x' + ($r.B - $r.T))
    $idx = $idx + 1
}

if (@($script:titles).Count -eq 0) {
    $ok = Take-Screenshot $shotPath
    if ($ok) { Say ('没发现可见窗口，已截全屏: ' + $shotPath + '  ← 请把这张图发回来') }
    else { Say '截图失败    : 无法自动截图，请把屏幕上的情况抄回来' }
    Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    Say ('报告已写入: ' + $reportPath)
    exit 2
}

# 错误对话框识别：唯一可见窗口的标题 == exe 文件名（如「DeskSpace.exe」）。
# 应用自己的窗口标题是「DeskSpace」或「DeskSpace 快速启动」，绝不会带 .exe 后缀；
# 标题等于文件名的是系统弹的报错对话框（典型就是缺 .NET 运行时）。
$exeFileName = [System.IO.Path]::GetFileName($Exe)
$isErrorDialog = $false
foreach ($t in $script:titles) {
    if ($t -eq $exeFileName) { $isErrorDialog = $true }
}

if ($isErrorDialog) {
    Say '结果      : ⚠ 启动被拦下了 —— 弹出了系统报错对话框（标题「' + $exeFileName + '」）'
    if ($edition -like 'portable*' -and -not $rt) {
        Say '原因判断  : 这台机器没有 .NET 10 桌面运行时，便携版打不开 —— 失败是预期内的'
        Say '            现在要验证的是：这个提示本身够不够清楚'
    }
    $ok = Take-Screenshot $shotPath
    if ($ok) { Say ('已截全屏  : ' + $shotPath + '  ← 请把这张图一起发回来，我要看对话框里的文字') }
    else { Say '截图失败    : 无法自动截图，请把对话框上的文字抄回来发我' }
    Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
    Say ('报告已写入: ' + $reportPath)
    exit 2
}
# 窗口中心点是否落在某一块屏幕的可视范围内（第二步要验的"窗口不跑出屏幕"）
try {
    Add-Type -AssemblyName System.Windows.Forms
    $r = $script:rects[0]
    $cx = [math]::Round(($r.L + $r.R) / 2)
    $cy = [math]::Round(($r.T + $r.B) / 2)
    $inside = $false
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        $b = $s.Bounds
        if ($cx -ge $b.X -and $cx -lt ($b.X + $b.Width) -and $cy -ge $b.Y -and $cy -lt ($b.Y + $b.Height)) { $inside = $true }
    }
    if ($inside) { Say ('窗口定位  : ✓ 中心点 (' + $cx + ',' + $cy + ') 在某块屏幕范围内，没有跑出屏幕') }
    else { Say ('窗口定位  : ✗ 中心点 (' + $cx + ',' + $cy + ') 不在任何屏幕内 —— 请把这个数字和显示器列表发回来') }
} catch {
    Say ('窗口定位  : 无法判定（' + $_.Exception.Message + '）')
}

$wpf = Get-ChildItem -Path $extractRoot -Filter 'wpfgfx_cor3.dll' -Recurse -ErrorAction SilentlyContinue
if ($wpf) {
    Say ('运行时来源: 自带（解压出 wpfgfx_cor3.dll）→ ' + ($wpf | Select-Object -First 1).FullName)
} else {
    Say '运行时来源: 使用系统已装的 .NET（解压目录里没有原生组件）'
}

if (-not $KeepRunning) {
    Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    Say '已结束 DeskSpace（-KeepRunning 可留着不关）'
}

Say ''
Say '==================== 体检结束 ===================='
Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8
Say ('报告已写入: ' + $reportPath)
exit 0
