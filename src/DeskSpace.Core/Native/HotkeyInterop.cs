using System.Runtime.InteropServices;

namespace DeskSpace.Core;

/// <summary>
/// 全局快捷键的 Win32 互操作。只使用 <c>RegisterHotKey</c>：
/// 不注册单独的 Win 键，不使用键盘钩子，不拦截系统按键行为。
/// </summary>
public static class HotkeyInterop
{
    public const int WM_HOTKEY = 0x0312;

    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED：组合键已被其他程序占用。</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>ERROR_INVALID_WINDOW_HANDLE。</summary>
    public const int ErrorInvalidWindowHandle = 1400;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
