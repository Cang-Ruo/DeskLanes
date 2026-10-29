#Requires -Version 5.1
<#
  probe-sidebar.ps1 —— 本机验证侧边栏收起态的按钮显示

  背景：虚拟机截图暴露缺陷 —— 侧边栏收起态窗口只有 MinWidth=44 的窄条，
  但顶部那排按钮（展开/停靠/全屏/悬浮/关闭）没有绑定可见性、始终显示，必然溢出。
  修复：XAML 里给该 StackPanel 加 Visibility 绑定到 IsExpanded，收起态整体隐藏。

  本脚本做四件事：
    1. 备份 settings.json，改成 WindowMode=Sidebar + SidebarExpanded=false（收起态）
    2. 轮询等侧边栏窗口出现（最多 40 秒，避免时序误判）
    3. UIAutomation 列出窗口内所有按钮的名字/位置/是否离屏；截图
    4. 结束后对比 errors.log 行数（NaN 缺陷修复后不应再增长），恢复配置

  用法：powershell -NoProfile -ExecutionPolicy Bypass -File probe-sidebar.ps1 -Exe <路径>
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
$cfgDir = Join-Path $env:LOCALAPPDATA 'DeskLanes'
$cfgFile = Join-Path $cfgDir 'settings.json'
$bakFile = Join-Path $cfgDir 'settings.json.probe-sidebar.bak'
$errLog = Join-Path $cfgDir 'logs\errors.log'
$shotPath = Join-Path $env:TEMP 'sidebar-probe.png'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Sb {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[void][Sb]::SetProcessDPIAware()

function Say($m) { Write-Host $m }

function Count-ErrorLines {
    if (Test-Path -LiteralPath $errLog) {
        return @(Get-Content -LiteralPath $errLog).Count
    }
    return 0
}

# 1. 备份并改配置
if (Test-Path -LiteralPath $cfgFile) { Copy-Item -LiteralPath $cfgFile -Destination $bakFile -Force }
$json = Get-Content -LiteralPath $cfgFile -Raw
$json = $json -replace '"WindowMode":\s*\d+', '"WindowMode": 2'
$json = $json -replace '"SidebarExpanded":\s*(true|false)', '"SidebarExpanded": false'
Set-Content -LiteralPath $cfgFile -Value $json -Encoding UTF8
Say '配置已改为 WindowMode=Sidebar、SidebarExpanded=false（收起态）'

# 2. 启动
Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
$errBefore = Count-ErrorLines
$proc = Start-Process -FilePath $Exe -PassThru
Say ('启动: ' + $Exe + '  PID=' + $proc.Id)

# 3. 轮询等侧边栏窗口（最多 40 秒）
$script:found = [IntPtr]::Zero
$script:targetPid = [uint32]$proc.Id
$cb = [Sb+EnumProc]{
    param($h, $l)
    $p2 = 0
    [void][Sb]::GetWindowThreadProcessId($h, [ref]$p2)
    if ($p2 -eq $script:targetPid -and [Sb]::IsWindowVisible($h)) {
        $script:found = $h
    }
    return $true
}
$deadline = (Get-Date).AddSeconds(40)
while ($script:found -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
    [void][Sb]::EnumWindows($cb, [IntPtr]::Zero)
    if ($script:found -eq [IntPtr]::Zero) { Start-Sleep -Seconds 2 }
}

if ($script:found -eq [IntPtr]::Zero) {
    Say '40 秒内没等到侧边栏窗口'
    Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path -LiteralPath $bakFile) { Copy-Item -LiteralPath $bakFile -Destination $cfgFile -Force }
    exit 1
}

$r = New-Object Sb+RECT
[void][Sb]::GetWindowRect($script:found, [ref]$r)
$w = $r.R - $r.L
$h = $r.B - $r.T
Say ('侧边栏窗口: (' + $r.L + ',' + $r.T + ')-(' + $r.R + ',' + $r.B + ')  宽=' + $w + ' 高=' + $h)
Say '  （本机屏幕宽 2560，贴右停靠的窄条左边缘应在 2500 附近）'

# 4. UIA 列出按钮
Add-Type -AssemblyName UIAutomationClient
$el = [System.Windows.Automation.AutomationElement]::FromHandle($script:found)
if ($el) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $btns = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    Say ('窗口内按钮总数: ' + $btns.Count)
    $vis = 0
    foreach ($b in $btns) {
        $nm = $b.Current.Name
        $off = $b.Current.IsOffscreen
        if (-not $off) { $vis = $vis + 1 }
        $br = $b.Current.BoundingRectangle
        Say ('  按钮「' + $nm + '」 离屏=' + $off + ' 矩形=(' + [int]$br.X + ',' + [int]$br.Y + ',' + [int]$br.Width + 'x' + [int]$br.Height + ')')
    }
    Say ('可见按钮数: ' + $vis + '  ← 修复后应为 1（只剩收起态的展开按钮）')
    if ($vis -eq 1) { Say '结论: PASS 收起态只保留一个展开按钮，顶部按钮排已按收起态隐藏' }
    else { Say '结论: FAIL 可见按钮数不是 1' }
} else {
    Say 'UIA 拿不到窗口元素'
}

# 5. 截图（屏幕右侧一条）
try {
    Add-Type -AssemblyName System.Drawing
    $sw = [Sb]::GetSystemMetrics(0)   # SM_CXSCREEN
    $sh = [Sb]::GetSystemMetrics(1)   # SM_CYSCREEN
    $cropW = [Math]::Min(400, $sw)
    # 注意：New-Object 的参数列表里不能直接写 ($sw - $cropW, ...)，
    # PowerShell 会把减号解析成方法名 op_Subtraction；必须先算成变量
    $cropLeft = $sw - $cropW
    $crop = New-Object System.Drawing.Rectangle -ArgumentList $cropLeft, 0, $cropW, $sh
    $bmp = New-Object System.Drawing.Bitmap $crop.Width, $crop.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($crop.Location, [System.Drawing.Point]::Empty, $crop.Size)
    $bmp.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Say ('已截图（屏幕右侧 ' + $cropW + 'px，屏幕 ' + $sw + 'x' + $sh + '）: ' + $shotPath)
} catch {
    Say ('截图失败: ' + $_.Exception.Message)
}

# 6. 退出并对比 errors.log
Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
$errAfter = Count-ErrorLines
Say ('errors.log 行数: ' + $errBefore + ' -> ' + $errAfter)
if ($errAfter -gt $errBefore) {
    Say '结论: FAIL 退出过程又写了新错误（看下面尾部）'
    if (Test-Path -LiteralPath $errLog) { Get-Content -LiteralPath $errLog -Tail 6 | ForEach-Object { Say ('    ' + $_) } }
} else {
    Say '结论: PASS 退出过程没有新增错误（NaN 保存缺陷已修复）'
}

Start-Sleep -Seconds 1
if (Test-Path -LiteralPath $bakFile) { Copy-Item -LiteralPath $bakFile -Destination $cfgFile -Force; Remove-Item -LiteralPath $bakFile -Force }
Say '配置已恢复'
exit 0
