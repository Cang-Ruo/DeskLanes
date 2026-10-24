#Requires -Version 5.1
<#
  verify-tray-icon.ps1 —— 托盘图标存在性 + 外观取证（Win11 隐藏溢出区）

  步骤：
    1. 启动 publish/win-x64/DeskSpace.exe
    2. Shell_NotifyIconGetRect((DeskSpaceMessageHost, 0x5201)) —— 注册存在性证据
    3. UIA Invoke「显示隐藏的图标」打开溢出浮层（不动鼠标）
    4. 在浮层里找名为 DeskSpace 的按钮，取 BoundingRectangle 截图
    5. Esc 收起浮层，结束进程

  注意：实测 FindWindow(null, 'DeskSpaceMessageHost') 找不到这个 WPF 窗口
  （EnumWindows 却能枚举到），所以用 EnumWindows 按标题 + 进程号定位。

  安全：只启动/结束 DeskSpace 自己；不移动鼠标、不改任何系统设置。
#>
param(
    [string]$ExePath = 'C:\Users\TTjustTT\Documents\Github\DeskSpace\publish\win-x64\DeskSpace.exe',
    [string]$OutDir  = 'C:\Users\TTjustTT\Documents\Github\DeskSpace\docs\stage7-evidence'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct TI_RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct NOTIFYICONIDENTIFIER
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public Guid guidItem;
}

public static class TI
{
    [DllImport("shell32.dll")]
    public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out TI_RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}
'@

[void][TI]::SetProcessDPIAware()

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TIEnum
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    public static IntPtr FindByTitle(uint targetPid, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid) {
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                if (t.ToString() == title) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Save-RectPng([System.Windows.Rect]$r, [string]$path, [int]$margin) {
    $x = [int][Math]::Floor($r.X) - $margin
    $y = [int][Math]::Floor($r.Y) - $margin
    $w = [int][Math]::Ceiling($r.Width) + 2 * $margin
    $h = [int][Math]::Ceiling($r.Height) + 2 * $margin
    if ($w -le 0 -or $h -le 0) { throw ("矩形非法: {0}x{1}" -f $w, $h) }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen((New-Object System.Drawing.Point($x, $y)),
                          [System.Drawing.Point]::Empty,
                          (New-Object System.Drawing.Size($w, $h)))
    } finally { $g.Dispose() }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return @{ X = $x; Y = $y; W = $w; H = $h }
}

Write-Output '===== 托盘图标取证（Win11 隐藏溢出区） ====='

Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$p = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p.Refresh()
Write-Output ("应用已启动 pid={0} HasExited={1}" -f $p.Id, $p.HasExited)

# ---- 1. 注册存在性 ----
$hostHwnd = [TIEnum]::FindByTitle($p.Id, 'DeskSpaceMessageHost')
Write-Output ("DeskSpaceMessageHost = 0x{0:X}" -f $hostHwnd.ToInt64())
if ($hostHwnd.ToInt64() -eq 0) { Write-Output '未找到消息窗口'; if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }; exit 1 }

$id = New-Object NOTIFYICONIDENTIFIER
$id.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][NOTIFYICONIDENTIFIER])
$id.hWnd = $hostHwnd
$id.uID = 0x5201
$id.guidItem = [Guid]::Empty

$rect = New-Object TI_RECT
$hr = [TI]::Shell_NotifyIconGetRect([ref]$id, [ref]$rect)
Write-Output ("Shell_NotifyIconGetRect HRESULT = 0x{0:X8}" -f $hr)
if ($hr -eq 0) {
    Write-Output ("注册矩形 = ({0},{1})-({2},{3})" -f $rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
    Write-Output '=> S_OK：该 (hWnd,uID) 的图标已注册进通知区域'
} else {
    Write-Output '=> E_FAIL：通知区域查不到该图标（异常，需要排查）'
}

# ---- 2. 打开溢出浮层（UIA Invoke，不动鼠标） ----
$root = [System.Windows.Automation.AutomationElement]::RootElement
$shellTray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')))
if ($null -eq $shellTray) { Write-Output '找不到 Shell_TrayWnd'; if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }; exit 1 }

$chevron = $shellTray.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '显示隐藏的图标')))
if ($null -eq $chevron) { Write-Output '找不到「显示隐藏的图标」按钮'; if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }; exit 1 }

$invoke = $chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
Start-Sleep -Milliseconds 1500
Write-Output '已通过 UIA Invoke 打开溢出浮层'

# ---- 2.5 打开浮层后的全屏截图 + 新出现的顶层窗口 ----
$pb2 = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$full = New-Object System.Drawing.Bitmap($pb2.Width, $pb2.Height)
$fg = [System.Drawing.Graphics]::FromImage($full)
try { $fg.CopyFromScreen($pb2.Location, [System.Drawing.Point]::Empty, $pb2.Size) } finally { $fg.Dispose() }
$full.Save((Join-Path $OutDir 'tray-flyout-open-full.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$full.Dispose()
Write-Output '已保存 tray-flyout-open-full.png（浮层打开时的全屏）'

Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
public static class TIWin
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    public static ArrayList List()
    {
        var list = new ArrayList();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            list.Add(string.Format("0x{0:X}\tclass={1}\ttitle={2}", h.ToInt64(), c, t));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
Write-Output '可见顶层窗口（找浮层窗口）：'
foreach ($line in [TIWin]::List()) { Write-Output ("    {0}" -f $line) }

# ---- 3. 在浮层里找名为 DeskSpace 的按钮 ----
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, 'DeskSpace')

$target = $null
$topWindows = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)
Write-Output ("顶层窗口数 = {0}" -f $topWindows.Count)
foreach ($w in $topWindows) {
    $c = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    if ($null -ne $c -and $c.Current.ControlType.ProgrammaticName -match 'Button') {
        Write-Output ("    候选: 窗口[{0}/{1}] 内元素 Name={2} Type={3}" -f $w.Current.Name, $w.Current.ClassName, $c.Current.Name, $c.Current.ControlType.ProgrammaticName)
        $target = $c
        break
    }
}

if ($null -ne $target) {
    $r = $target.Current.BoundingRectangle
    Write-Output ("DeskSpace 托盘按钮矩形 = ({0:N0},{1:N0}) {2:N0}x{3:N0}" -f $r.X, $r.Y, $r.Width, $r.Height)
    try {
        $info = Save-RectPng $r (Join-Path $OutDir 'tray-icon-flyout.png') 6
        Write-Output ("已保存 tray-icon-flyout.png  rect=({0},{1}) {2}x{3}" -f $info.X, $info.Y, $info.W, $info.H)
        Write-Output '=> 托盘图标取证成功（浮层中找到 DeskSpace 按钮）'
    } catch {
        Write-Output ("截图失败: {0}" -f $_.Exception.Message)
    }
} else {
    Write-Output '=> 浮层中未找到 Name=DeskSpace 的按钮（取证失败）'
}

# ---- 4. 收起浮层、结束进程 ----
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 500
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep -Milliseconds 600 }
Write-Output ("已结束 DeskSpace (pid {0}); 残留进程数 = {1}" -f $p.Id, @(Get-Process -Name DeskSpace -ErrorAction SilentlyContinue).Count)
Write-Output '===== 结束 ====='
