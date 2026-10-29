# 诊断：用 MSAA（IAccessible + AccessibleChildren）枚举原生弹出菜单项
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace DsWinB {
    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"),
     InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IAccessible {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object get_accParent();
        int get_accChildCount();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object get_accChild([In, MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accName([In, MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accValue([In, MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDescription([In, MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accRole([In, MarshalAs(UnmanagedType.Struct)] object varChild);
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accState([In, MarshalAs(UnmanagedType.Struct)] object varChild);
    }

    public static class N {
        public static List<string> Wins = new List<string>();
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        public static string Describe(IntPtr h) {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            var tb = new StringBuilder(256);
            GetWindowText(h, tb, 256);
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            return string.Format("0x{0:X}|{1}|{2}|{3}|{4}", h.ToInt64(), sb.ToString(), tb.ToString(), IsWindowVisible(h), pid);
        }
        [DllImport("oleacc.dll")]
        public static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, [In] ref Guid riid, out IAccessible ppvObject);
        [DllImport("oleacc.dll")]
        public static extern int AccessibleChildren(IAccessible paccContainer, int iChildStart, int cChildren,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 2)] object[] rgvarChildren,
            out int pcObtained);
        public const uint ObjidMenu = 3;
        public static Guid IidIAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
    }
}
"@

$cb = { param($h, $l) [DsWinB.N]::Wins.Add([DsWinB.N]::Describe($h)); return $true }
function Enum-All {
    [DsWinB.N]::Wins.Clear()
    [void][DsWinB.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWinB.N]::Wins
}
function Find-Row([string]$title, [int]$ownerPid) {
    foreach ($line in (Enum-All)) {
        $p = $line.Split('|')
        if (($p[2] -eq $title) -and ([int]$p[4] -eq $ownerPid)) {
            return [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16))
        }
    }
    return [IntPtr]::Zero
}

$proc = Get-Process -Name DeskLanes -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskLanes 未运行"; exit }
$hostH = Find-Row "DeskLanesMessageHost" $proc.Id
[DsWinB.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 900

$menuH = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('|')
    if (($p[1] -eq "#32768") -and ([int]$p[4] -eq $proc.Id)) { $menuH = [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16)) }
}
if ($menuH -eq [IntPtr]::Zero) { Write-Host "菜单未弹出"; exit }
Write-Host ("菜单窗口 = 0x{0:X}" -f $menuH.ToInt64())

$guid = [DsWinB.N]::IidIAccessible
$acc = $null
$hr = [DsWinB.N]::AccessibleObjectFromWindow($menuH, [DsWinB.N]::ObjidMenu, [ref]$guid, [ref]$acc)
Write-Host ("AccessibleObjectFromWindow hr=0x{0:X}" -f $hr)
if ($acc -eq $null) { Write-Host "拿不到 IAccessible"; exit }
$count = $acc.get_accChildCount()
Write-Host ("accChildCount = {0}" -f $count)
Write-Host ("accName(CHILDID_SELF=0) = {0}" -f $acc.get_accName(0))

Write-Host "=== 逐个 child.Read Name ==="
for ($i = 1; $i -le $count; $i++) {
    $role = $acc.get_accRole($i)
    $state = $acc.get_accState($i)
    Write-Host ("  [{0}] name=[{1}] role={2} state={3}" -f $i, $acc.get_accName($i), $role, $state)
}

Write-Host "=== AccessibleChildren ==="
$children = New-Object object[] $count
$obtained = 0
$hr2 = [DsWinB.N]::AccessibleChildren($acc, 0, $count, $children, [ref]$obtained)
Write-Host ("AccessibleChildren hr=0x{0:X} obtained={1}" -f $hr2, $obtained)
for ($i = 0; $i -lt $obtained; $i++) {
    $c = $children[$i]
    if ($c -is [DsWinB.IAccessible]) {
        $ia = [DsWinB.IAccessible]$c
        Write-Host ("  child[{0}] name=[{1}] role={2}" -f $i, $ia.get_accName(0), $ia.get_accRole(0))
    } else {
        Write-Host ("  child[{0}] 非 IAccessible：{1}" -f $i, $c)
    }
}
Write-Host "done"
