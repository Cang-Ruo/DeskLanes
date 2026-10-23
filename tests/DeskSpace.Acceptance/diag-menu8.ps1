# 诊断：把 MSAA 菜单枚举完全放进 C# 里执行（PowerShell 无法直接调用自定义 COM 接口方法）
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace DsWinC {
    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"),
     InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IAccessible {
        [PreserveSig]
        int GetTypeInfoCount(out uint pctinfo);
        [PreserveSig]
        int GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo);
        [PreserveSig]
        int GetIDsOfNames(ref Guid riid, [MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.LPWStr, SizeParamIndex=2)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgDispId);
        [PreserveSig]
        int Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
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
        public const uint ObjidMenu = 3;
        public static Guid IidIAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

        /// <summary>返回每行 "序号|文本|role|state"。</summary>
        public static string[] MenuRowsViaMsaa(IntPtr menuHwnd) {
            Guid riid = IidIAccessible;
            IAccessible acc;
            int hr = AccessibleObjectFromWindow(menuHwnd, ObjidMenu, ref riid, out acc);
            if (hr != 0 || acc == null) { return new string[] { "ERROR|hr=0x" + hr.ToString("X") }; }
            int count;
            try { count = acc.get_accChildCount(); } catch (Exception e) { return new string[] { "ERROR|count:" + e.Message }; }
            var rows = new List<string>();
            for (int i = 1; i <= count; i++) {
                string name = "";
                string role = "";
                string state = "";
                try { name = acc.get_accName(i) ?? ""; } catch { }
                try { object r = acc.get_accRole(i); role = (r == null) ? "" : r.ToString(); } catch { }
                try { object s = acc.get_accState(i); state = (s == null) ? "" : s.ToString(); } catch { }
                rows.Add(string.Format("{0}|{1}|{2}|{3}", i, name, role, state));
            }
            return rows.ToArray();
        }
    }
}
"@

$cb = { param($h, $l) [DsWinC.N]::Wins.Add([DsWinC.N]::Describe($h)); return $true }
function Enum-All {
    [DsWinC.N]::Wins.Clear()
    [void][DsWinC.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWinC.N]::Wins
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

$proc = Get-Process -Name DeskSpace -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -eq $null) { Write-Host "DeskSpace 未运行"; exit }
$hostH = Find-Row "DeskSpaceMessageHost" $proc.Id
[DsWinC.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 900

$menuH = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('|')
    if (($p[1] -eq "#32768") -and ([int]$p[4] -eq $proc.Id)) { $menuH = [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16)) }
}
if ($menuH -eq [IntPtr]::Zero) { Write-Host "菜单未弹出"; exit }
Write-Host ("菜单窗口 = 0x{0:X}" -f $menuH.ToInt64())
foreach ($row in [DsWinC.N]::MenuRowsViaMsaa($menuH)) {
    Write-Host ("  {0}" -f $row)
}
Write-Host "done"
