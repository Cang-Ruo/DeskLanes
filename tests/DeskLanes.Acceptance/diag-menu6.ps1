# 诊断：外部进程读取原生弹出菜单内容的其它途径（GetMenuBarInfo / MSAA）
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace DsWinA {
    public static class N {
        public static System.Collections.Generic.List<string> Wins = new System.Collections.Generic.List<string>();
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
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

        // 途径一：GetMenuBarInfo(OBJID_MENU)
        public const uint ObjidMenu = 3;
        [StructLayout(LayoutKind.Sequential)]
        public struct MENUBARINFO {
            public int cbSize;
            public RECT rcBar;
            public IntPtr hMenu;
            public IntPtr hwndMenu;
            public int fBarFocused;
            public int fFocused;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        public static extern bool GetMenuBarInfo(IntPtr hwnd, uint idObject, uint idItem, ref MENUBARINFO pmbi);
        [DllImport("user32.dll")]
        public static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int nMaxChars, uint flags);
        public const uint MfByPosition = 0x400;

        public static string TryGetMenuBarInfo(IntPtr menuHwnd) {
            var log = new StringBuilder();
            var mbi = new MENUBARINFO();
            mbi.cbSize = Marshal.SizeOf(typeof(MENUBARINFO));
            bool ok = GetMenuBarInfo(menuHwnd, ObjidMenu, 0, ref mbi);
            log.AppendLine("GetMenuBarInfo ok=" + ok + " hMenu=" + mbi.hMenu.ToInt64() + " hwndMenu=" + mbi.hwndMenu.ToInt64());
            if (ok && mbi.hMenu != IntPtr.Zero) {
                int count = GetMenuItemCount(mbi.hMenu);
                log.AppendLine("  GetMenuItemCount=" + count);
                for (int i = 0; i < count; i++) {
                    var sb = new StringBuilder(256);
                    GetMenuString(mbi.hMenu, (uint)i, sb, 256, MfByPosition);
                    log.AppendLine("   [" + i + "] " + sb.ToString());
                }
            }
            return log.ToString();
        }

        // 途径二：MSAA AccessibleObjectFromWindow(OBJID_MENU)
        [DllImport("oleacc.dll")]
        public static extern int AccessibleObjectFromWindow(
            IntPtr hwnd, uint dwId, [In] ref Guid riid,
            [MarshalAs(UnmanagedType.IDispatch)] out object ppvObject);
        public static Guid IidIAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
    }
}
"@

$cb = { param($h, $l) [DsWinA.N]::Wins.Add([DsWinA.N]::Describe($h)); return $true }
function Enum-All {
    [DsWinA.N]::Wins.Clear()
    [void][DsWinA.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWinA.N]::Wins
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
[DsWinA.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 900

$menuH = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('|')
    if (($p[1] -eq "#32768") -and ([int]$p[4] -eq $proc.Id)) { $menuH = [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16)) }
}
if ($menuH -eq [IntPtr]::Zero) { Write-Host "菜单未弹出"; exit }
Write-Host ("菜单窗口 = 0x{0:X}" -f $menuH.ToInt64())

Write-Host "=== 途径一：GetMenuBarInfo ==="
Write-Host ([DsWinA.N]::TryGetMenuBarInfo($menuH))

Write-Host "=== 途径二：MSAA ==="
$guid = [DsWinA.N]::IidIAccessible
$acc = $null
$hr = [DsWinA.N]::AccessibleObjectFromWindow($menuH, [DsWinA.N]::ObjidMenu, [ref]$guid, [ref]$acc)
Write-Host ("AccessibleObjectFromWindow hr=0x{0:X} acc={1}" -f $hr, ($acc -ne $null))
if ($acc -ne $null) {
    Write-Host ("accChildCount = {0}" -f $acc.accChildCount)
    Write-Host ("accName(0) = {0}" -f $acc.accName(0))
    try {
        $kids = $acc.accChild
        Write-Host ("accChild = {0}" -f $kids)
    } catch { Write-Host ("accChild 读取失败：{0}" -f $_.Exception.Message) }
}
Write-Host "done"
