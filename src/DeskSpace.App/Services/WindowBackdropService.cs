using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DeskSpace.Core;

namespace DeskSpace.App.Services;

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
    /// 在窗口构造函数里调用一次：SourceInitialized 后自动应用背景效果。
    /// 幂等；对分层窗口是无操作。
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) => Apply(window);
    }

    /// <summary>对已创建句柄的窗口应用背景效果。返回是否应用了原生 DWM 背景。</summary>
    public static bool Apply(Window window)
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
        int round = DwmInterop.CornerPreferenceRound;
        _ = DwmInterop.DwmSetWindowAttribute(
            hwnd, DwmInterop.AttributeCornerPreference, ref round, sizeof(int));

        if (!DwmInterop.SupportsSystemBackdrop || DwmInterop.IsRemoteSession)
        {
            FallbackToOpaque(window);
            return false;
        }

        // [实验] Mica 在本机（Build 26300）实测可渲染；Acrylic 不渲染（客户区呈中性灰）。
        // 所以顺序为 Mica 优先，Acrylic 仅作备用。
        if (!TrySetBackdrop(hwnd, DwmInterop.BackdropMica))
        {
            TrySetBackdrop(hwnd, DwmInterop.BackdropAcrylic);
        }

        // 标题栏底色：延伸玻璃框后原生标题栏区域会透出后面的窗口（无材料），
        // 给它一个与玻璃主题一致的深色，视觉上与 Mica 客户区衔接
        var captionColor = new DwmInterop.ColorRef(0x1E, 0x30, 0x46);
        _ = DwmInterop.DwmSetWindowAttributeColor(
            hwnd, DwmInterop.AttributeCaptionColor, ref captionColor);

        // 读回校验：确认系统真的接受了背景类型，才延伸玻璃框；
        // 否则在旧系统上延伸玻璃框会把客户区染成黑色。
        if (DwmInterop.DwmGetWindowAttribute(
                hwnd, DwmInterop.AttributeSystemBackdropType, out int applied, sizeof(int)) == 0
            && applied > 0)
        {
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
                // 窗口基底：Mica 会染上壁纸色调，叠一层低透明度的雾蓝灰，
                // 让窗口在不同壁纸上都保持统一的主题色（壁纸仍隐约可见）
                window.Background = FindThemeBrush("BrushWindowBaseGlass") ?? Brushes.Transparent;
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
