using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DeskLanes.Core;

namespace DeskLanes.App.Services;

/// <summary>
/// 轻玻璃窗口背景的统一入口（全应用只此一处系统互操作）。
/// <para>
/// 两类窗口、两条路径：
/// 1. 普通窗口（MainWindow / SettingsWindow）：应用 Win11 22H2+ 的 DWM 系统背景材料，
///    优先 Acrylic（磨砂，贴近"轻玻璃"），失败退 Mica；确认背景类型写入成功后才把玻璃框
///    延伸进客户区并让 WPF 背景透明——任何一步失败都把窗口背景还原为不透明底色，
///    保证窗口永远可见（规格 3.3 的回退要求）。
/// 2. 分层窗口（FloatingWindow / SidebarWindow，AllowsTransparency=True）：DWM 系统背景
///    与分层窗口不兼容，直接使用各窗口 XAML 里配置的高不透明度玻璃底色（文字保持全不透明），
///    本服务不做任何处理。
/// </para>
/// </summary>
public static class WindowBackdropService
{
    /// <summary>原生背景不可用时的回退底色（雾蓝灰深色，与主题玻璃面板同源）。</summary>
    private static readonly Color FallbackColor = Color.FromRgb(0x1E, 0x30, 0x46);

    /// <summary>
    /// 诊断开关：环境变量 <c>DESKLANES_BACKDROP</c> 强制指定背景材质，
    /// 用于排查「材质没生效」这类只在特定机器上复现的问题。
    /// 取值 <c>auto|none|mica|acrylic|tabbed</c>（大小写不敏感）；未设置或非法值时走默认策略。
    /// </summary>
    private static int? ForcedBackdrop
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable("DESKLANES_BACKDROP");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return raw.Trim().ToLowerInvariant() switch
            {
                "auto" => DwmInterop.BackdropAuto,
                "none" => DwmInterop.BackdropNone,
                "mica" => DwmInterop.BackdropMica,
                "acrylic" => DwmInterop.BackdropAcrylic,
                "tabbed" => DwmInterop.BackdropTabbed,
                _ => null,
            };
        }
    }

    /// <summary>
    /// 把本次背景决策写进诊断文件，便于在别的机器上取证（日志目录）。
    /// 失败不影响主流程。
    /// </summary>
    private static void WriteProbe(string line)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskLanes");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "backdrop-probe.txt"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
        }
        catch
        {
            // 诊断写入失败不应当影响窗口显示
        }
    }

    /// <summary>
    /// 在窗口构造函数里调用一次：SourceInitialized 后自动应用背景效果。
    /// 幂等；对分层窗口是无操作。
    /// </summary>
    /// <param name="squareCorner">
    /// 贴屏幕边缘停靠的窗口（侧边栏）传 true：系统圆角会把贴边那侧的角切掉一块，
    /// 看着像缺角。这个是公开的 DWMWA_WINDOW_CORNER_PREFERENCE，不涉及未公开接口。
    /// </param>
    public static void Attach(Window window, bool squareCorner = false, bool paintWindowSurface = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) => Apply(window, squareCorner, paintWindowSurface);
    }

    /// <summary>对已创建句柄的窗口应用背景效果。返回是否应用了原生 DWM 背景。</summary>
    /// <param name="paintWindowSurface">
    /// true（默认）：由窗口自身背景画基底 tint，适合没有圆角需求的普通窗口。
    /// false：窗口自身背景留空，由带圆角的子 Border 画一次——
    /// 悬浮窗 / 侧边栏用这个，避免同一像素叠两层 tint。
    /// </param>
    public static bool Apply(Window window, bool squareCorner = false, bool paintWindowSurface = true)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (window.AllowsTransparency)
        {
            // 分层窗口：不用 DWM 背景，玻璃底色由 XAML 负责
            return false;
        }

        // 标题栏深色模式（白字）与系统圆角：失败不影响主流程
        int on = 1;
        _ = DwmInterop.DwmSetWindowAttribute(
            hwnd, DwmInterop.AttributeUseImmersiveDarkMode, ref on, sizeof(int));
        int round = squareCorner
            ? DwmInterop.CornerPreferenceDoNotRound
            : DwmInterop.CornerPreferenceRound;
        _ = DwmInterop.DwmSetWindowAttribute(
            hwnd, DwmInterop.AttributeCornerPreference, ref round, sizeof(int));

        if (!DwmInterop.SupportsSystemBackdrop || DwmInterop.IsRemoteSession)
        {
            FallbackToOpaque(window);
            return false;
        }

        int? forced = ForcedBackdrop;
        if (forced.HasValue)
        {
            // 诊断模式：不问对错，强制设置成指定值，用来观察它到底渲染成什么样
            int value = forced.Value;
            int hr = DwmInterop.DwmSetWindowAttribute(
                hwnd, DwmInterop.AttributeSystemBackdropType, ref value, sizeof(int));
            WriteProbe(string.Format(
                CultureInfo.InvariantCulture,
                "forced backdrop={0} hr=0x{1:X8}",
                value, hr));
        }
        else if (TrySetBackdrop(hwnd, DwmInterop.BackdropAcrylic))
        {
            // 首选 Desktop Acrylic：真正的半透明磨砂玻璃，能看到模糊后的桌面内容
            WriteProbe("acrylic accepted");
        }
        else
        {
            // Acrylic 被拒 → 退 Mica（不透明，只把壁纸颜色融进背景）
            WriteProbe("acrylic rejected -> fallback to mica");
            TrySetBackdrop(hwnd, DwmInterop.BackdropMica);
        }

        // 读回校验：确认系统真的接受了背景类型，才延伸玻璃框；
        // 否则在旧系统上延伸玻璃框会把客户区染成黑色。
        // 注意 auto(0) 也是合法值，所以判据是「不是 none(1)」而不是「大于 0」。
        int hrGet = DwmInterop.DwmGetWindowAttribute(
            hwnd, DwmInterop.AttributeSystemBackdropType, out int applied, sizeof(int));
        WriteProbe(string.Format(
            CultureInfo.InvariantCulture,
            "getback hr=0x{0:X8} applied={1} (0=auto 1=none 2=mica 3=acrylic 4=tabbed)",
            hrGet, applied));

        if (hrGet == 0 && applied != DwmInterop.BackdropNone)
        {
            // 标题栏底色：Acrylic 让标题栏同样透出材质（COLOR_NONE），
            // 否则原生标题栏会是一块与玻璃区域割裂的深色实心条；
            // Mica / 回退路径仍用深色与客户区衔接。
            if (applied == DwmInterop.BackdropAcrylic)
            {
                int none = DwmInterop.ColorNone;
                _ = DwmInterop.DwmSetWindowAttribute(
                    hwnd, DwmInterop.AttributeCaptionColor, ref none, sizeof(int));
            }
            else
            {
                var captionColor = new DwmInterop.ColorRef(0x1E, 0x30, 0x46);
                _ = DwmInterop.DwmSetWindowAttributeColor(
                    hwnd, DwmInterop.AttributeCaptionColor, ref captionColor);
            }
            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget == null)
            {
                FallbackToOpaque(window);
                return false;
            }

            // 关键一步：WPF 非分层窗口的合成目标默认是黑色不透明表面，
            // 只把 Window.Background 设为透明不够，必须把合成目标背景也设为透明，
            // DWM 的背景材料才会透出来（与微软 WPF Mica 示例一致）。
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var margins = new DwmInterop.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmInterop.DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0)
            {
                // 窗口基底：叠一层低透明度的雾蓝灰，让窗口在不同壁纸上都保持统一的主题色。
                //
                // paintWindowSurface=false 的窗口（悬浮窗 / 侧边栏）自己用一个**带圆角**的
                // Border 画这层底色：如果这里再画一遍，同一像素上就叠了两层 tint
                // （18% + 18% ≈ 33%），玻璃会明显变闷。所以这种情况下把窗口自身背景留空，
                // 交给子元素画一次。
                window.Background = paintWindowSurface
                    ? FindThemeBrush("BrushWindowBaseGlass") ?? Brushes.Transparent
                    : Brushes.Transparent;
                return true;
            }
        }

        FallbackToOpaque(window);
        return false;
    }

    private static bool TrySetBackdrop(IntPtr hwnd, int backdropType)
    {
        int value = backdropType;
        return DwmInterop.DwmSetWindowAttribute(
            hwnd, DwmInterop.AttributeSystemBackdropType, ref value, sizeof(int)) == 0;
    }

    /// <summary>从应用级主题资源里取画刷（快照工具 / 宿主环境缺资源时给 null）。</summary>
    private static Brush? FindThemeBrush(string key)
    {
        return System.Windows.Application.Current?.TryFindResource(key) as Brush;
    }

    private static void FallbackToOpaque(Window window)
    {
        var brush = new SolidColorBrush(FallbackColor);
        brush.Freeze();
        window.Background = brush;
    }
}
