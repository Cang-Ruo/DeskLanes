#Requires -Version 5.1
<#
  verify-toggle-command.ps1 —— 对照实验：WM_COMMAND 0x7402（资源管理器右键菜单
  「查看 → 显示桌面图标」同款命令）能否真正隐藏/显示桌面图标

  判据：SHELLDLL_DefView 的 IsWindowVisible + SysListView32 项数 + 图标区截图。
  安全：0x7402 是切换命令，测试里发两次（一隐一显）保证回到原状态；
       不重启 Explorer；不动桌面文件。
#>
param(
    [string]$OutDir = 'C:\Users\TTjustTT\Documents\Github\DeskLanes\docs\stage7-evidence'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TG
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

    public static int ListViewCount(IntPtr listView)
    {
        return (int)SendMessage(listView, 0x1004, IntPtr.Zero, IntPtr.Zero);
    }
}
'@

[void][TG]::SetProcessDPIAware()

function Get-State {
    $progman = [TG]::FindTopWindowByClass('Progman')
    if ($progman.ToInt64() -eq 0) { return @{ DefView = [IntPtr]::Zero; Visible = $null; Icons = -1 } }
    $dv = [TG]::FindDefView($progman)
    if ($dv.ToInt64() -eq 0) { return @{ DefView = [IntPtr]::Zero; Visible = $null; Icons = -1 } }
    $lv = [TG]::FindWindowEx($dv, [IntPtr]::Zero, 'SysListView32', [NullString]::Value)
    return @{
        DefView = $dv
        Visible = [TG]::IsWindowVisible($dv)
        Icons   = $(if ($lv.ToInt64() -ne 0) { [TG]::ListViewCount($lv) } else { -1 })
    }
}

function Save-IconArea([string]$name) {
    Start-Sleep -Milliseconds 900
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size) } finally { $g.Dispose() }
    $full = Join-Path $OutDir $name
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Wait-Visible([bool]$want, [int]$timeoutMs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $s = Get-State
        if ($s.Visible -eq $want) { return @{ Ok = $true; Ms = $sw.ElapsedMilliseconds; State = $s } }
        Start-Sleep -Milliseconds 200
    }
    return @{ Ok = $false; Ms = $sw.ElapsedMilliseconds; State = (Get-State) }
}

Write-Output '===== WM_COMMAND 0x7402 切换桌面图标对照实验 ====='

$s0 = Get-State
Write-Output ("[初始] DefView 可见={0} 图标数={1}" -f $s0.Visible, $s0.Icons)
if ($s0.DefView.ToInt64() -eq 0) { Write-Output '未找到 DefView，中止'; exit 1 }
$progman = [TG]::FindTopWindowByClass('Progman')

# WM_COMMAND 0x7402 = 切换「显示桌面图标」
try {
    Write-Output '--- 发送 WM_COMMAND 0x7402 到 DefView（第 1 次，期望隐藏） ---'
    [void][TG]::SendMessage($s0.DefView, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero)
    $r = Wait-Visible $false 5000
    Write-Output ("    DefView 隐藏={0}（{1} ms）；图标数={2}" -f $r.Ok, $r.Ms, $r.State.Icons)
    Save-IconArea 'tg-1-after-toggle1.png'

    Write-Output '--- 发送 WM_COMMAND 0x7402 到 DefView（第 2 次，期望恢复） ---'
    $sNow = Get-State
    [void][TG]::SendMessage($sNow.DefView, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero)
    $r2 = Wait-Visible $true 5000
    Write-Output ("    DefView 可见={0}（{1} ms）；图标数={2}" -f $r2.Ok, $r2.Ms, $r2.State.Icons)
    Save-IconArea 'tg-2-after-toggle2.png'
}
finally {
    $s = Get-State
    Write-Output ("[结束状态] DefView 可见={0} 图标数={1}" -f $s.Visible, $s.Icons)
    if ($s.Visible -ne $true) {
        Write-Output '状态与初始不一致，补发一次切换命令...'
        [void][TG]::SendMessage($progman, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 1500
        $s2 = Get-State
        Write-Output ("[补发后] DefView 可见={0} 图标数={1}" -f $s2.Visible, $s2.Icons)
    }
}
Write-Output '===== 实验结束 ====='
