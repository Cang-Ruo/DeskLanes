#Requires -Version 5.1
<#
  probe-defview.ps1 —— 探测桌面窗口结构与图标数量
  Progman / WorkerW → SHELLDLL_DefView → SysListView32 → LVM_GETITEMCOUNT
  图标数量是"桌面图标是否显示"的权威判据（隐藏时 ListView 项数为 0）
#>
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

public static class DV
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    public struct R { public int L, T, Rt, B; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls, string title);

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

    public static ArrayList Children(IntPtr parent)
    {
        var list = new ArrayList();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            list.Add(string.Format("0x{0:X} vis={1} class={2}", h.ToInt64(), IsWindowVisible(h), c));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr FindDefViewUnder(IntPtr parent)
    {
        IntPtr w = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (c.ToString() == "SHELLDLL_DefView") { w = h; return false; }
            return true;
        }, IntPtr.Zero);
        return w;
    }

    public static int ListViewCount(IntPtr listView)
    {
        // LVM_GETITEMCOUNT = 0x1004
        return (int)SendMessage(listView, 0x1004, IntPtr.Zero, IntPtr.Zero);
    }
}
'@
[void][DV]::SetProcessDPIAware()

Write-Output '===== 桌面窗口结构探测 ====='
$progman = [DV]::FindWindow('Progman', $null)
Write-Output ("Progman = 0x{0:X}  visible={1}" -f $progman.ToInt64(), [DV]::IsWindowVisible($progman))

$dv = [DV]::FindDefViewUnder($progman)
Write-Output ("Progman 下的 SHELLDLL_DefView = 0x{0:X}" -f $dv.ToInt64())

if ($dv.ToInt64() -ne 0) {
    $lv = [DV]::FindWindowEx($dv, [IntPtr]::Zero, 'SysListView32', $null)
    Write-Output ("  SysListView32 = 0x{0:X}  visible={1}" -f $lv.ToInt64(), [DV]::IsWindowVisible($lv))
    if ($lv.ToInt64() -ne 0) {
        Write-Output ("  图标数量 = {0}" -f [DV]::ListViewCount($lv))
    }
}

Write-Output '--- WorkerW 分支 ---'
$w = [DV]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, 'WorkerW', $null)
$idx = 0
while ($w.ToInt64() -ne 0) {
    $dv2 = [DV]::FindDefViewUnder($w)
    Write-Output ("WorkerW#{0} = 0x{1:X} visible={2} 含DefView=0x{3:X}" -f $idx, $w.ToInt64(), [DV]::IsWindowVisible($w), $dv2.ToInt64())
    if ($dv2.ToInt64() -ne 0) {
        $lv2 = [DV]::FindWindowEx($dv2, [IntPtr]::Zero, 'SysListView32', $null)
        if ($lv2.ToInt64() -ne 0) {
            Write-Output ("  SysListView32 visible={0} 图标数量={1}" -f [DV]::IsWindowVisible($lv2), [DV]::ListViewCount($lv2))
        }
    }
    $w = [DV]::FindWindowEx([IntPtr]::Zero, $w, 'WorkerW', $null)
    $idx++
}

Write-Output '--- Progman 直接子窗口 ---'
foreach ($l in [DV]::Children($progman)) { Write-Output ("    {0}" -f $l) }
Write-Output '===== 探测结束 ====='
