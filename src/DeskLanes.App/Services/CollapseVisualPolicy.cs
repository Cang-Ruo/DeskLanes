using DeskLanes.Core.Models;

namespace DeskLanes.App.Services;

/// <summary>阶段 34：三种窗口模式收束后的指示器形态。</summary>
public enum CollapseVisual
{
    /// <summary>全屏：默认隐藏，鼠标靠近左边界才弹出半透明窄条，点击展开。</summary>
    FullStrip,

    /// <summary>侧边栏：常驻 0.5cm 半透明边栏，悬停即展开、移开收束。</summary>
    SidebarBar,

    /// <summary>悬浮窗：DeskLanes Glass Orb，冰川蓝双轨流线冰晶球。</summary>
    FloatingBall,
}

/// <summary>
/// 阶段 34 起：收束尺寸与交互策略的纯计算（不碰 WPF，冒烟可直接覆盖）。
/// 侧栏 / 全屏窄条仍用物理像素；阶段 38 的 Glass Orb 统一用 DIP 表达，
/// 只有落盘位置和 SetWindowPos 用物理像素，避免混用 Window.Left/Top。
/// </summary>
public static class CollapseVisualPolicy
{
    /// <summary>侧栏边栏 / 全屏窄条的可见宽度：0.5cm。</summary>
    public const double BarCentimeters = 0.5;

    private const double PixelsPerCentimeter = 96.0 / 2.54;

    public static double CentimetersToPixels(double centimeters)
        => centimeters * PixelsPerCentimeter;

    /// <summary>0.5cm ≈ 19 物理像素。</summary>
    public static int BarPixels => (int)Math.Round(CentimetersToPixels(BarCentimeters));

    /// <summary>
    /// 全屏的热区宽度（窄条的两倍）：鼠标「靠近」左边界而不是精确压在 19px 上才触发。
    /// </summary>
    public static int FullHotZonePixels => BarPixels * 2;

    // ---------- 阶段 38：DeskLanes Glass Orb ----------

    /// <summary>可视直径 44 DIP（静止 / 悬停）。</summary>
    public const double OrbVisualDip = 44;

    /// <summary>拖动时可视直径 46 DIP，配合 1.05 缩放。</summary>
    public const double OrbDragVisualDip = 46;

    /// <summary>
    /// 鼠标命中热区 56 DIP（≥ 规格要求的 48 DIP）——这是唯一的点击区域。
    /// </summary>
    public const double OrbHitDip = 56;

    /// <summary>
    /// 阶段 42：渲染窗口 76 DIP，比命中区每边多 10 DIP，
    /// 让 Crystal Aura 有空间向外扩散，外缘 alpha=0 不与窗口边界相交。
    /// 鼠标命中仍只有中心 56 DIP，点击范围不变。
    /// </summary>
    public const double OrbRenderDip = 76;

    /// <summary>长按 350ms 后才具备拖动资格。</summary>
    public const int OrbLongPressMs = 350;

    /// <summary>长按成立后还要移动 6 DIP 才真正进入拖动。</summary>
    public const double OrbDragThresholdDip = 6;

    /// <summary>三态之间的颜色 / 缩放缓动时长。</summary>
    public const double OrbTransitionSeconds = 0.14;

    /// <summary>Tooltip 延迟 550ms（规格 500~600ms 中值）。</summary>
    public const int OrbTooltipDelayMs = 550;

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
    /// （隐藏窗口 + 对应的半透明指示器），不能再用老的两条局部路径。
    /// 设置窗口打开期间（suppress=true）保持展开，优先级最高。
    /// </summary>
    public static bool ShouldUseCollapseIndicator(bool autoCollapse, bool suppress)
        => autoCollapse && !suppress;

    /// <summary>只有悬浮窗小球可以拖动，边栏 / 窄条固定在左边界。</summary>
    public static bool CanDrag(CollapseVisual visual) => visual == CollapseVisual.FloatingBall;

    // ---------- 阶段 38：交互状态机 ----------

    /// <summary>Glass Orb 的交互状态：Idle → Hover → PressPending → Dragging。</summary>
    public enum OrbInteractionState { Idle, Hover, PressPending, Dragging }

    /// <summary>Glass Orb 的三种视觉状态。</summary>
    public enum OrbVisualState { Idle, Hover, Dragging }

    /// <summary>
    /// 长按达标（≥350ms）且移动超过 6 DIP 才允许进入拖动。
    /// 两个条件缺一不可，避免手抖或快速点击误触。
    /// </summary>
    public static bool CanBeginOrbDrag(long elapsedMs, double distanceDip)
        => elapsedMs >= OrbLongPressMs && distanceDip >= OrbDragThresholdDip;

    /// <summary>PressPending 沿用 Hover 的视觉，不额外闪烁。</summary>
    public static OrbVisualState VisualForInteraction(OrbInteractionState state) => state switch
    {
        OrbInteractionState.Dragging => OrbVisualState.Dragging,
        OrbInteractionState.Idle => OrbVisualState.Idle,
        _ => OrbVisualState.Hover,
    };
}
