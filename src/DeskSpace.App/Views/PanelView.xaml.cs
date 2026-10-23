using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DeskSpace.App.ViewModels;
using DeskSpace.Core.Models;

namespace DeskSpace.App.Views;

/// <summary>
/// 单个分区面板：标题栏按住拖动移动，右下角手柄拖动缩放，项目区接受拖放。
/// 打开/移除等命令由外层 <see cref="FreePanelsViewModel"/> 通过依赖属性注入。
/// </summary>
public partial class PanelView : UserControl
{
    public static readonly DependencyProperty OpenItemCommandProperty =
        DependencyProperty.Register(nameof(OpenItemCommand), typeof(ICommand), typeof(PanelView));

    public static readonly DependencyProperty RemoveItemCommandProperty =
        DependencyProperty.Register(nameof(RemoveItemCommand), typeof(ICommand), typeof(PanelView));

    public static readonly DependencyProperty ReturnToPoolCommandProperty =
        DependencyProperty.Register(nameof(ReturnToPoolCommand), typeof(ICommand), typeof(PanelView));

    /// <summary>请求选中本面板（标题栏交互时由外层视图处理）。</summary>
    public event EventHandler? SelectionRequested;

    public PanelView()
    {
        InitializeComponent();
    }

    public ICommand? OpenItemCommand
    {
        get => (ICommand?)GetValue(OpenItemCommandProperty);
        set => SetValue(OpenItemCommandProperty, value);
    }

    public ICommand? RemoveItemCommand
    {
        get => (ICommand?)GetValue(RemoveItemCommandProperty);
        set => SetValue(RemoveItemCommandProperty, value);
    }

    public ICommand? ReturnToPoolCommand
    {
        get => (ICommand?)GetValue(ReturnToPoolCommandProperty);
        set => SetValue(ReturnToPoolCommandProperty, value);
    }

    private PanelViewModel? ViewModel => DataContext as PanelViewModel;

    /// <summary>
    /// 视图渲染缩放系数（虚拟单位 → 屏幕像素）。优先取画布宿主的实时缩放；
    /// 找不到宿主时退化为"实际宽度 / 虚拟宽度"（独立渲染场景）。
    /// </summary>
    private double Scale
    {
        get
        {
            if (FindCanvasHost() is FreePanelsView host && host.CanvasScaleFactor > 0)
            {
                return host.CanvasScaleFactor;
            }

            double virtualWidth = ViewModel?.Model.Width ?? 0;
            return virtualWidth > 0 && PanelBorder.ActualWidth > 0
                ? PanelBorder.ActualWidth / virtualWidth
                : 1.0;
        }
    }

    private FreePanelsView? FindCanvasHost()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is FreePanelsView view)
            {
                return view;
            }

            current = current is FrameworkElement fe && fe.Parent is DependencyObject parent
                ? parent
                : System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SelectSelf()
    {
        if (ViewModel is not null && FindOwner() is FreePanelsViewModel owner)
        {
            owner.SelectedPanel = ViewModel;
        }

        SelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPanelMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => SelectSelf();

    private void OnMoveThumbPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => SelectSelf();

    private void OnMoveDragStarted(object sender, DragStartedEventArgs e)
        => SelectSelf();

    private void OnMoveDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        double scale = Math.Max(Scale, 0.01);
        ViewModel.X += e.HorizontalChange / scale;
        ViewModel.Y += e.VerticalChange / scale;
    }

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        double scale = Math.Max(Scale, 0.01);
        ViewModel.Width += e.HorizontalChange / scale;
        ViewModel.Height += e.VerticalChange / scale;
    }

    private void OnItemDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropFormats.GetItemId(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>把拖拽的项目放入本分区。</summary>
    private void OnItemDrop(object sender, DragEventArgs e)
    {
        string? itemId = DragDropFormats.GetItemId(e.Data);
        if (itemId is null || DataContext is not PanelViewModel panel)
        {
            return;
        }

        if (FindOwner() is FreePanelsViewModel owner)
        {
            DesktopItem? item = owner.FindItemById(itemId);
            if (item is not null)
            {
                owner.AssignToPanel(item, panel);
            }
        }

        e.Handled = true;
    }

    private FreePanelsViewModel? FindOwner()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: FreePanelsViewModel vm })
            {
                return vm;
            }

            current = current is FrameworkElement fe && fe.Parent is DependencyObject parent
                ? parent
                : System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
