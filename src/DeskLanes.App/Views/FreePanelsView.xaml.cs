using System.Windows;
using System.Windows.Controls;
using DeskLanes.App.ViewModels;
using DeskLanes.Core.Models;

namespace DeskLanes.App.Views;

/// <summary>
/// 自由分区视图：虚拟画布上的可拖拽/缩放分区 + 底部未分区项目池 + 移除区。
/// 交互逻辑都转发给 <see cref="FreePanelsViewModel"/>。
/// </summary>
public partial class FreePanelsView : UserControl
{
    public FreePanelsView()
    {
        InitializeComponent();
    }

    /// <summary>当前画布缩放系数（虚拟单位 → 屏幕像素）。拖拽换算用。</summary>
    public double CanvasScaleFactor { get; private set; } = 1;

    /// <summary>画布宿主尺寸变化时重算缩放（虚拟 1600×900 → 实际像素，居中放置）。</summary>
    private void OnCanvasHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double scaleX = CanvasHost.ActualWidth / WorkspacePanel.CanvasWidth;
        double scaleY = CanvasHost.ActualHeight / WorkspacePanel.CanvasHeight;
        double scale = Math.Min(scaleX, scaleY);
        if (double.IsNaN(scale) || scale <= 0)
        {
            scale = 0.01;
        }

        CanvasScaleFactor = scale;
        CanvasScale.ScaleX = scale;
        CanvasScale.ScaleY = scale;
        CanvasOffset.X = (CanvasHost.ActualWidth - WorkspacePanel.CanvasWidth * scale) / 2;
        CanvasOffset.Y = (CanvasHost.ActualHeight - WorkspacePanel.CanvasHeight * scale) / 2;
    }

    private void OnPoolDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropFormats.GetItemId(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>拖到项目池：把项目放回未分区。</summary>
    private void OnPoolDrop(object sender, DragEventArgs e)
    {
        string? itemId = DragDropFormats.GetItemId(e.Data);
        if (itemId is null || DataContext is not FreePanelsViewModel viewModel)
        {
            return;
        }

        DesktopItem? item = viewModel.FindItemById(itemId);
        if (item is not null)
        {
            viewModel.AssignToPool(item);
        }

        e.Handled = true;
    }

    private void OnRemoveDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropFormats.GetItemId(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>拖到移除区：从工作区移除（有确认对话框，不动桌面文件）。</summary>
    private void OnRemoveDrop(object sender, DragEventArgs e)
    {
        string? itemId = DragDropFormats.GetItemId(e.Data);
        if (itemId is null || DataContext is not FreePanelsViewModel viewModel)
        {
            return;
        }

        DesktopItem? item = viewModel.FindItemById(itemId);
        if (item is not null && viewModel.RemoveItemCommand.CanExecute(item))
        {
            viewModel.RemoveItemCommand.Execute(item);
        }

        e.Handled = true;
    }
    /// <summary>拖到画布空白处：PanelView 会先接住并处理，只有空白区域才到这里。</summary>
    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropFormats.GetItemId(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        string? itemId = DragDropFormats.GetItemId(e.Data);
        if (itemId is null || DataContext is not FreePanelsViewModel viewModel)
        {
            return;
        }

        DesktopItem? item = viewModel.FindItemById(itemId);
        if (item is null)
        {
            return;
        }

        // CanvasBorder 做了 Scale + Translate，落点要逆变换回 1600×900 虚拟坐标。
        double scale = Math.Max(CanvasScaleFactor, 0.01);
        Point hostPoint = e.GetPosition(CanvasHost);
        double x = (hostPoint.X - CanvasOffset.X) / scale;
        double y = (hostPoint.Y - CanvasOffset.Y) / scale;
        viewModel.CreatePanelAt(item, x, y);
        e.Handled = true;
    }

}
