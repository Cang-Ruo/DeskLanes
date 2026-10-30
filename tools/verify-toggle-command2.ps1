#Requires -Version 5.1
<#
  verify-toggle-command2.ps1 —— 精细时序实验：WM_COMMAND 0x7402 发给 DefView
  采样：DefView 可见性 / ListView 图标数 / HKCU HideIcons 值 / 截图
  安全：0x7402 是切换命令，测试发两次保证回到原状态；不重启 Explorer；不动桌面文件。
#>
param(
    [string]$OutDir = "$env:USERPROFILE\Documents\Github\DeskLanes\docs\stage7-evidence"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TG2
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

[void][TG2]::SetProcessDPIAware()

$advKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'

function Get-HideIcons {
    $v = Get-ItemProperty -Path $advKey -Name HideIcons -ErrorAction SilentlyContinue
    if ($null -eq $v) { return 'ABSENT' }
    return [int]$v.HideIcons
}

function Get-DefView {
    $progman = [TG2]::FindTopWindowByClass('Progman')
    if ($progman.ToInt64() -eq 0) { return [IntPtr]::Zero }
    $dv = [TG2]::FindDefView($progman)
    if ($dv.ToInt64() -ne 0) { return $dv }
    $w = [TG2]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', [NullString]::Value)
    while ($w.ToInt64() -ne 0) {
        $dv2 = [TG2]::FindDefView($w)
        if ($dv2.ToInt64() -ne 0) { return $dv2 }
        $w = [TG2]::FindWindowEx([IntPtr]::Zero, $w, 'WorkerW', [NullString]::Value)
    }
    return [IntPtr]::Zero
}

function Get-Icons([IntPtr]$dv) {
    $lv = [TG2]::FindWindowEx($dv, [IntPtr]::Zero, 'SysListView32', [NullString]::Value)
    if ($lv.ToInt64() -eq 0) { return -1 }
    return [TG2]::ListViewCount($lv)
}

function Sample([string]$label) {
    $dv = Get-DefView
    $vis = $(if ($dv.ToInt64() -eq 0) { '<no-defview>' } else { [TG2]::IsWindowVisible($dv) })
    $n = Get-Icons $dv
    Write-Output ("[{0}] DefView可见={1} 图标数={2} HideIcons={3}" -f $label, $vis, $n, (Get-HideIcons))
}

function Save-Shot([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size) } finally { $g.Dispose() }
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Write-Output '===== 0x7402 精细时序实验 ====='
Sample '初始'

$dv = Get-DefView
if ($dv.ToInt64() -eq 0) { Write-Output '未找到 DefView，中止'; exit 1 }

Write-Output '--- 切换 1（期望隐藏） ---'
[void][TG2]::SendMessage($dv, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500;  Sample 't+0.5s'
Save-Shot 'tg2-1-toggle1.png'
Start-Sleep -Seconds 2;         Sample 't+2.5s'
Start-Sleep -Seconds 3;         Sample 't+5.5s'

Write-Output '--- 切换 2（期望恢复） ---'
$dv = Get-DefView
[void][TG2]::SendMessage($dv, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500;  Sample 't+0.5s'
Save-Shot 'tg2-2-toggle2.png'
Start-Sleep -Seconds 2;         Sample 't+2.5s'
Start-Sleep -Seconds 3;         Sample 't+5.5s'

Write-Output '--- 终态 ---'
Sample '终态'
Write-Output '===== 实验结束 ====='
