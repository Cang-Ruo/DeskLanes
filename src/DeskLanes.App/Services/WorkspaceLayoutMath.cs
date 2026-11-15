using DeskLanes.Core.Models;

namespace DeskLanes.App.Services;

/// <summary>
/// 阶段 32：全屏布局的纯计算（不碰 WPF 对象，冒烟测试可直接覆盖）。
/// 拆出来是因为「2cm 边距 / 搜索框对齐」以前只藏在 code-behind 里，
/// 换分辨率后很容易悄悄跑偏却没有测试能发现。
/// </summary>
public static class WorkspaceLayoutMath
{
    /// <summary>目标左右留白：2 厘米（物理像素，按 96 DPI 换算成 DIP 后再除以系统 DPI 缩放）。</summary>
    public const double TargetSideMarginCentimeters = 2.0;

    private const double DipPerCentimeter = 96.0 / 2.54;

    /// <summary>2cm 对应的物理像素（与 DPI 无关）。</summary>
    public static double TargetSideMarginPixels => TargetSideMarginCentimeters * DipPerCentimeter;

    /// <summary>2cm 在指定 DPI 缩放下对应的 DIP 数。</summary>
    public static double TargetSideMarginDip(double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            dpiScale = 1;
        }

        return TargetSideMarginPixels / dpiScale;
    }

    /// <summary>
    /// 画布缩放：在「高度约束（原有的 scaleY*1.12）」和「2cm 边距反推的缩放」
    /// 之间取较大者，再以 scaleX 封顶，保证画布永远不会被横向裁掉。
    /// </summary>
    public static double ComputeCanvasScale(double hostWidth, double hostHeight, double dpiScale)
    {
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return 0.01;
        }

        double scaleX = hostWidth / WorkspacePanel.CanvasWidth;
        double scaleY = hostHeight / WorkspacePanel.CanvasHeight;
        double marginDip = TargetSideMarginDip(dpiScale);
        double scaleByMargin = Math.Max(0.01,
            (hostWidth - 2 * marginDip) / WorkspacePanel.CanvasWidth);
        double scale = Math.Min(scaleX, Math.Max(scaleY * 1.12, scaleByMargin));
        return double.IsNaN(scale) || scale <= 0 ? 0.01 : scale;
    }

    /// <summary>给定缩放后，画布左右实际留白（DIP）。</summary>
    public static double SideMarginDip(double hostWidth, double scale)
        => Math.Max(0, (hostWidth - WorkspacePanel.CanvasWidth * scale) / 2);

    /// <summary>
    /// 搜索框宽度：右边缘对齐工具栏提示文字右边缘（相对窗口坐标），
    /// 减去根 Grid 左内边距；结果钳在 [300, 可用宽度-28]。
    /// </summary>
    public static double AlignSearchWidth(double hintRightX, double leftInset, double windowWidth)
        => Math.Clamp(Math.Round(hintRightX - leftInset), 300,
            Math.Max(300, windowWidth - 28));
}
