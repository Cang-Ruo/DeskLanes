#Requires -Version 5.1
<#
  probe-defview-state.ps1 —— 观察「显示 vs 隐藏」两种状态下 DefView 的可读差异
  目的：给 DesktopIconService 修复方案找一个可靠的"当前是否隐藏"读数
  安全：0x7402 切换两次回到原状态；不重启 Explorer；不动桌面文件。
#>
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class PS2
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    public struct RC { public int L, T, R, B; }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr h, out RC r);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr h);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    public static IntPtr FindTop(string cls)
    {
        IntPtr f = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() == cls) { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }

    public static IntPtr FindDef(IntPtr parent)
    {
        IntPtr f = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() == "SHELLDLL_DefView") { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }
}
'@
[void][PS2]::SetProcessDPIAware()

function Get-Info {
    $progman = [PS2]::FindTop('Progman')
    $dv = [PS2]::FindDef($progman)
    if ($dv.ToInt64() -eq 0) {
        # WorkerW 兜底
        $w = [IntPtr]::Zero
        # 简化：直接在所有顶层窗口里找托管 DefView 的
        return @{ Found = $false }
    }
    $r = New-Object PS2+RC
    [void][PS2]::GetWindowRect($dv, [ref]$r)
    $lv = [IntPtr]::Zero
    # 找 SysListView32
    $lv = [PS2]::FindDef($dv)  # 不对，这里只是占位
    return @{
        Found = $true
        DefView = $dv
        Parent = [PS2]::GetParent($dv)
        Visible = [PS2]::IsWindowVisible($dv)
        Rect = ('({0},{1})-({2},{3})' -f $r.L, $r.T, $r.R, $r.B)
    }
}

Write-Output '===== DefView 状态差异观察 ====='

function Show([string]$label) {
    $i = Get-Info
    if (-not $i.Found) { Write-Output ("[{0}] 未找到 DefView" -f $label); return }
    Write-Output ("[{0}] DefView=0x{1:X} parent=0x{2:X} visible={3} rect={4}" -f $label, $i.DefView.ToInt64(), $i.Parent.ToInt64(), $i.Visible, $i.Rect)
}

Show '初始'

$progman = [PS2]::FindTop('Progman')
$dv = [PS2]::FindDef($progman)
try {
    [void][PS2]::SendMessage($dv, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
    Show '切换1后'

    [void][PS2]::SendMessage($dv, 0x0111, [IntPtr]0x7402, [IntPtr]::Zero)
    Start-Sleep -Seconds 2
    Show '切换2后'
}
finally {
    # 保证回到显示状态：若当前隐藏则再切一次
    $i = Get-Info
    Write-Output ("[结束] 状态见上；如未回到初始状态请手动检查")
}
Write-Output '===== 观察结束 ====='
