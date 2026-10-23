using System;
using System.Windows;
using DeskSpace.App.Services;
using DeskSpace.App.ViewModels;

namespace DeskSpace.App.Views;

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

        double height = Math.Min(screenHeight * 0.85, 760);
        Height = height;
        Top = screenTop + Math.Max(0, (screenHeight - height) / 2);

        Left = _viewModel.IsDockedRight ? screenLeft + screenWidth - Width : screenLeft;
    }

    private void OnDeactivated(object? sender, EventArgs e)
        => _viewModel.OnWindowDeactivated();
}
