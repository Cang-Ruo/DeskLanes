using System;
using System.Runtime.InteropServices;

namespace DeskLanes.Core;

/// <summary>
/// DWM 窗口背景（Win11 22H2+ 系统背景材料）与玻璃延伸相关的 Win32 互操作。
/// 只封装系统调用，不含任何策略判断；使用方请走 App 层的 WindowBackdropService，
/// 避免在多个窗口重复编写系统互操作代码。
/// </summary>
public static class DwmInterop
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int sizeOfValue);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int sizeOfValue);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE：标题栏深色模式（白字）。</summary>
    public const int AttributeUseImmersiveDarkMode = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE：窗口圆角偏好。</summary>
    public const int AttributeCornerPreference = 33;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE：系统背景材料（Win11 22H2 起支持）。</summary>
    public const int AttributeSystemBackdropType = 38;

    /// <summary>DWMWA_CAPTION_COLOR：标题栏背景色（Win11 起）。</summary>
    public const int AttributeCaptionColor = 35;

    /// <summary>DWMWA_BORDER_COLOR：窗口边框颜色（Win11 起）。</summary>
    public const int AttributeBorderColor = 34;

    /// <summary>DWMWCP_ROUND：系统圆角。</summary>
    public const int CornerPreferenceRound = 2;

    /// <summary>DWMSBT_AUTO：交给系统决定（通常等价于 Mica）。</summary>
    public const int BackdropAuto = 0;

    /// <summary>DWMSBT_NONE：不使用系统背景材料。</summary>
    public const int BackdropNone = 1;

    /// <summary>DWMSBT_MAINWINDOW：Mica 材料（随壁纸着色，无实时模糊）。</summary>
    public const int BackdropMica = 2;

    /// <summary>DWMSBT_TRANSIENTWINDOW：Acrylic 材料（磨砂模糊，更贴近"轻玻璃"定位）。</summary>
    public const int BackdropAcrylic = 3;

    /// <summary>DWMSBT_TABBEDWINDOW：Mica Alt（比 Mica 更浓，用于多标签窗口）。</summary>
    public const int BackdropTabbed = 4;

    /// <summary>DWMWA_USE_HOSTBACKDROPBRUSH：Win10 1809+ 的旧式背景画刷（Acrylic 的前身）。</summary>
    public const int AttributeUseHostBackdropBrush = 17;

    /// <summary>DWMWA_COLOR_NONE：标题栏 / 边框不绘制颜色（透出背后的材质，0xFFFFFFFE）。</summary>
    public const int ColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>SM_REMOTESESSION：当前会话是否为远程桌面（远程会话下背景材料渲染不可靠）。</summary>
    public const int MetricRemoteSession = 90;

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    /// <summary>COLORREF（0x00BBGGRR）。用于标题栏 / 边框颜色属性。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ColorRef
    {
        public byte R;
        public byte G;
        public byte B;

        public ColorRef(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    /// <summary>按 COLORREF 语义写入颜色类属性（标题栏色、边框色）。</summary>
    public static int DwmSetWindowAttributeColor(IntPtr hwnd, int attribute, ref ColorRef value)
    {
        int packed = value.R | (value.G << 8) | (value.B << 16);
        return DwmSetWindowAttribute(hwnd, attribute, ref packed, sizeof(int));
    }

    /// <summary>Win11 22H2（Build 22621）起才支持 DWMWA_SYSTEMBACKDROP_TYPE。</summary>
    public static bool SupportsSystemBackdrop
    {
        get { return Environment.OSVersion.Version.Build >= 22621; }
    }

    /// <summary>当前会话是否运行在远程桌面里（RDP 下 Acrylic 渲染异常，直接走回退）。</summary>
    public static bool IsRemoteSession
    {
        get { return GetSystemMetrics(MetricRemoteSession) != 0; }
    }
}
