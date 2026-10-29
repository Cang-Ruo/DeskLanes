using System.Runtime.InteropServices;
using System.Text;

namespace DeskLanes.Core;

/// <summary>
/// 系统托盘图标的 Win32 互操作（Shell_NotifyIcon + 原生弹出菜单）。
/// 不引用 WinForms，避免与 WPF 的类型冲突；托盘行为完全自控。
/// </summary>
public static class TrayInterop
{
    public const int WmApp = 0x8000;

    /// <summary>托盘图标回调消息（落在承载窗口的消息循环上）。</summary>
    public const int TrayCallbackMessage = WmApp + 1;

    public const int WmCommand = 0x0111;

    public const int WmLButtonDblClk = 0x0203;

    public const int WmRButtonUp = 0x0205;

    /// <summary>NIN_SELECT：托盘图标被键盘/鼠标"选择"（新版 Windows 用）。</summary>
    public const int NinSelect = WmApp + 0;

    // NIM_*
    public const int NimAdd = 0;

    public const int NimModify = 1;

    public const int NimDelete = 2;

    // NIF_*
    public const int NifMessage = 0x01;

    public const int NifIcon = 0x02;

    public const int NifTip = 0x04;

    public const int NifInfo = 0x10;

    // NIIF_*
    public const int NiifInfo = 0x01;

    public const int NiifWarning = 0x02;

    public const int NiifError = 0x03;

    // TPM_*
    public const int TpmLeftAlign = 0x0000;

    public const int TpmRightButton = 0x0002;

    public const int TpmRightAlign = 0x0008;

    public const int TpmReturnCmd = 0x0100;

    public const int MfString = 0x0000;

    public const int MfSeparator = 0x0800;

    public const int IdiApplication = 32512;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIdNewItem, string lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int TrackPopupMenu(
        IntPtr hMenu, int uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint ExtractIconEx(
        string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>WM_NULL：什么都不做，只用来触发一次任务切换。</summary>
    public const int WmNull = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;

        public int Y;
    }

    /// <summary>NOTIFYICONDATAW（含 Windows 7+ 字段）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;

        public IntPtr hWnd;

        public int uID;

        public int uFlags;

        public int uCallbackMessage;

        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;

        public int dwStateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;

        public Guid guidItem;

        public IntPtr hBalloonIcon;
    }

    /// <summary>取当前光标位置（托盘菜单定位用）。</summary>
    public static POINT GetCursorPosition()
    {
        GetCursorPos(out POINT point);
        return point;
    }
}
