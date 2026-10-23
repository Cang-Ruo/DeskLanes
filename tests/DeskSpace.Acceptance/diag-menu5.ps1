# 诊断：为什么读不到 DeskSpace 原生托盘菜单的菜单项（定位 MN_GETHMENU 链路）
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace DsWin9 {
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
        // 普通做法：GetMenuItemCount + GetMenuItemInfoW(MIIM_STRING)
        public const uint MnGetHmenu = 0x01E5;
        public const uint MiimState = 0x1;
        public const uint MiimId = 0x2;
        public const uint MiimType = 0x10;
        public const uint MiimString = 0x40;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MENUITEMINFO {
            public int cbSize;
            public uint fMask;
            public uint fType;
            public uint fState;
            public uint wID;
            public IntPtr hSubMenu;
            public IntPtr hbmpChecked;
            public IntPtr hbmpUnchecked;
            public IntPtr dwItemData;
            public IntPtr dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }
        [DllImport("user32.dll")]
        public static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMenuItemInfo(IntPtr hMenu, int uItem, bool fByPosition, ref MENUITEMINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int nMaxChars, uint flags);
        [DllImport("user32.dll")]
        public static extern uint GetMenuItemID(IntPtr hMenu, int nPos);
        public static string Probe(IntPtr menuHwnd) {
            var log = new StringBuilder();
            IntPtr hMenu = SendMessage(menuHwnd, MnGetHmenu, IntPtr.Zero, IntPtr.Zero);
            log.AppendLine("MN_GETHMENU -> " + hMenu.ToInt64());
            if (hMenu == IntPtr.Zero) { return log.ToString(); }
            int count = GetMenuItemCount(hMenu);
            log.AppendLine("GetMenuItemCount -> " + count);
            for (int i = 0; i < count; i++) {
                // 方式一：GetMenuString（MF_BYPOSITION=0x400）
                var sb = new StringBuilder(256);
                int len = GetMenuString(hMenu, (uint)i, sb, 256, 0x400);
                log.AppendLine("  [" + i + "] GetMenuString len=" + len + " text=[" + sb.ToString() + "] id=" + GetMenuItemID(hMenu, i));
                // 方式二：GetMenuItemInfo
                var info = new MENUITEMINFO();
                info.cbSize = Marshal.SizeOf(typeof(MENUITEMINFO));
                info.fMask = MiimState | MiimId | MiimType | MiimString;
                info.dwTypeData = IntPtr.Zero;
                info.cch = 0;
                bool ok1 = GetMenuItemInfo(hMenu, i, true, ref info);
                log.AppendLine("      GetMenuItemInfo(长度探测) ok=" + ok1 + " cch=" + info.cch + " fType=" + info.fType + " wID=" + info.wID);
                IntPtr buf = Marshal.AllocHGlobal((int)((info.cch + 2) * 2));
                try {
                    info.cch = info.cch + 1;
                    info.dwTypeData = buf;
                    bool ok2 = GetMenuItemInfo(hMenu, i, true, ref info);
                    string t = Marshal.PtrToStringUni(buf) ?? "";
                    log.AppendLine("      GetMenuItemInfo(取文本) ok=" + ok2 + " text=[" + t + "]");
                } finally {
                    Marshal.FreeHGlobal(buf);
                }
            }
            return log.ToString();
        }
    }
}
"@

$cb = { param($h, $l) [DsWin9.N]::Wins.Add([DsWin9.N]::Describe($h)); return $true }
function Enum-All {
    [DsWin9.N]::Wins.Clear()
    [void][DsWin9.N]::EnumWindows($cb, [IntPtr]::Zero)
    return [DsWin9.N]::Wins
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
Write-Host ("宿主窗口 = 0x{0:X}" -f $hostH.ToInt64())
[DsWin9.N]::PostMessage($hostH, 0x8001, [IntPtr]::new(0x5201), [IntPtr]::new(0x0205)) | Out-Null
Start-Sleep -Milliseconds 900

$menuH = [IntPtr]::Zero
foreach ($line in (Enum-All)) {
    $p = $line.Split('|')
    if (($p[1] -eq "#32768") -and ([int]$p[4] -eq $proc.Id)) { $menuH = [IntPtr]::new([Convert]::ToInt64($p[0].Substring(2), 16)) }
}
if ($menuH -eq [IntPtr]::Zero) { Write-Host "菜单未弹出"; exit }
Write-Host ("菜单窗口 = 0x{0:X}" -f $menuH.ToInt64())
Write-Host ([DsWin9.N]::Probe($menuH))
Write-Host "done"
