using DeskLanes.Core.Models;

namespace DeskLanes.App.Services;

/// <summary>阶段 34：三种窗口模式收束后的指示器形态。</summary>
public enum CollapseVisual
{
    /// <summary>全屏：默认隐藏，鼠标靠近左边界才弹出半透明窄条，点击展开。</summary>
    FullStrip,

    /// <summary>侧边栏：常驻 0.5cm 半透明边栏，悬停即展开、移开收束。</summary>
    SidebarBar,

    /// <summary>悬浮窗：常驻直径 3cm 的半透明小球，点击展开。</summary>
    FloatingBall,
}

/// <summary>
/// 阶段 34：收束尺寸与交互策略的纯计算（不碰 WPF，冒烟可直接覆盖）。
/// 尺寸一律用物理像素表达，再按显示器 DPI 换算成 DIP，混合 DPI 下不会变形。
/// </summary>
public static class CollapseVisualPolicy
{
    /// <summary>侧栏边栏 / 全屏窄条的可见宽度：0.5cm。</summary>
    public const double BarCentimeters = 0.5;

    /// <summary>悬浮窗小球直径：3cm。</summary>
    public const double BallCentimeters = 3.0;

    private const double PixelsPerCentimeter = 96.0 / 2.54;

    public static double CentimetersToPixels(double centimeters)
        => centimeters * PixelsPerCentimeter;

    /// <summary>0.5cm ≈ 19 物理像素。</summary>
    public static int BarPixels => (int)Math.Round(CentimetersToPixels(BarCentimeters));

    /// <summary>3cm ≈ 113 物理像素。</summary>
    public static int BallPixels => (int)Math.Round(CentimetersToPixels(BallCentimeters));

    /// <summary>
    /// 全屏的热区宽度（窄条的两倍）：鼠标「靠近」左边界而不是精确压在 19px 上才触发，
    /// 否则太难点。可见窄条仍只有 0.5cm，靠左对齐。
    /// </summary>
    public static int FullHotZonePixels => BarPixels * 2;

    public static CollapseVisual ForMode(WindowMode mode) => mode switch
    {
        WindowMode.Sidebar => CollapseVisual.SidebarBar,
        WindowMode.Floating => CollapseVisual.FloatingBall,
        _ => CollapseVisual.FullStrip,
    };

    /// <summary>只有侧边栏是「鼠标移上去就展开」。</summary>
    public static bool ExpandsOnHover(WindowMode mode) => mode == WindowMode.Sidebar;

    /// <summary>全屏与悬浮窗都必须点击才展开。</summary>
    public static bool RequiresClick(WindowMode mode)
        => mode is WindowMode.Full or WindowMode.Floating;

    /// <summary>只有全屏的窄条默认隐藏，鼠标靠近左边界才弹出。</summary>
    public static bool HiddenUntilNearBoundary(WindowMode mode) => mode == WindowMode.Full;

    /// <summary>
    /// 阶段 35：勾了「失焦自动隐藏 / 自动收起」时，必须统一走收束入口
    /// （隐藏窗口 + 对应的半透明指示器），不能再用老的两条局部路径：
    /// 悬浮窗「完全隐藏到托盘」、侧边栏「只收成 44px 窄条」。
    /// 那两条路径会让用户切到别的软件后既看不到窗口、也看不到阶段 34 的小球/边栏。
    /// 设置窗口打开期间（suppress=true）保持展开，优先级最高。
    /// </summary>
    public static bool ShouldUseCollapseIndicator(bool autoCollapse, bool suppress)
        => autoCollapse && !suppress;
}
