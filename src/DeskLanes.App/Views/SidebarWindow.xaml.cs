using System;
using System.Windows;
using DeskLanes.Core;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using DeskLanes.App.Services;
using DeskLanes.App.ViewModels;
using DeskLanes.Core.Models;

namespace DeskLanes.App.Views;

/// <summary>
/// 侧边栏窗口：停靠在屏幕左侧或右侧，可展开/收起。
/// 位置由 <see cref="SidebarViewModel"/> 的停靠设置驱动（每次显示/切换停靠时重算），
/// 宽度变化时保持在屏幕内。失焦收起由视图模型按设置决定（视图只负责转达失焦事件）。
/// </summary>
public partial class SidebarWindow : Window
{
    private readonly SidebarViewModel _viewModel;
    private readonly AppController _controller;

    public SidebarWindow(SidebarViewModel viewModel, AppController controller)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        DataContext = viewModel;
        InitializeComponent();

        // 与主窗口 / 悬浮窗同一套 DWM Acrylic；贴边停靠所以不要系统圆角
        WindowBackdropService.Attach(this, squareCorner: true, paintWindowSurface: false);

        Deactivated += OnDeactivated;
        _viewModel.HideRequested += (_, _) => _controller.HideSidebar();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SidebarViewModel.IsExpanded) or nameof(SidebarViewModel.IsDockedRight))
            {
                ApplyWindowWidth();
                PlaceOnDockedSide();
            }
        };

        Loaded += (_, _) =>
        {
            ApplyWindowWidth();
            PlaceOnDockedSide();
        };

        ContentRendered += (_, _) => _ = _controller.EnsureInitializedAsync();
    }

    /// <summary>显示窗口并放置到停靠侧。</summary>
    public void ShowDocked()
    {
        // 阶段 21：上次收起过的话，直接切回来会出现「宽窗口 + 只剩一个 ◀ 按钮」的
        // 空白灰块（IsExpanded=false 但 Width 还停在展开宽度）。切到侧边栏模式时
        // 一律以展开态呈现，保证分类与项目一定看得见。
        if (!_viewModel.IsExpanded)
        {
            _viewModel.IsExpanded = true;
        }

        ApplyWindowWidth();
        PlaceOnDockedSide();
        Show();
        Activate();
    }

    /// <summary>展开时用设置里的宽度，收起时只留窄条。</summary>
    private void ApplyWindowWidth()
    {
        Width = _viewModel.IsExpanded ? _viewModel.ExpandedWidth : CollapsedWidth;
        MinWidth = CollapsedWidth;
    }

    private const double CollapsedWidth = 44;

    private void PlaceOnDockedSide()
    {
        // 用 SystemParameters 取当前虚拟桌面范围：多显示器 / DPI 变化后仍能落在可见区域内
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenWidth = SystemParameters.VirtualScreenWidth;
        double screenHeight = SystemParameters.VirtualScreenHeight;

        // 阶段 20：用户要「屏幕有多高就占多高」。
        // 不能直接取 VirtualScreenHeight —— 三屏时那是所有屏叠加的总高，
        // 窗口会被推到屏幕外（截图里只剩壁纸）。先按停靠侧定位，
        // 再取窗口所在那块显示器的工作区高度，按该屏 DPI 换算成 DIP。
        Left = _viewModel.IsDockedRight ? screenLeft + screenWidth - Width : screenLeft;
        Top = screenTop;

        // 阶段 22：Show 之前 HWND 可能还没创建，不能靠 MonitorFromWindow。
        // 直接按停靠侧（虚拟屏最左 / 最右）取那块屏的工作区，一次定位到位，
        // 不需要显示后再补一次（那正是切换时「跳一下」的来源）。
        if (MonitorInterop.TryGetWorkAreaForDockedSide(_viewModel.IsDockedRight,
                out MonitorInterop.RECT work, out uint dpi) && work.Height > 0)
        {
            double scale = Math.Max(dpi / 96.0, 0.01);
            Height = Math.Max(MinHeight, work.Height / scale);
            Top = work.Top / scale;
        }
        else
        {
            // 拿不到显示器信息时退回主屏工作区，绝不用虚拟屏总高
            Rect primaryWork = SystemParameters.WorkArea;
            Height = Math.Max(MinHeight, primaryWork.Height);
            Top = primaryWork.Top;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
        => _viewModel.OnWindowDeactivated();
    /// <summary>双击侧边栏项目启动（自定义行模板不走 ItemTile 的双击处理）。</summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: DesktopItem item }
            && DataContext is SidebarViewModel viewModel)
        {
            viewModel.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>阶段 26：整个项目区都能用滚轮滚动，不再只有右侧滚动条可滚。</summary>
    private void OnOuterPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer viewer)
        {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

}
