using System;
using System.Windows;
using System.Windows.Input;
using DeskSpace.App.Services;
using DeskSpace.App.ViewModels;

namespace DeskSpace.App.Views;

/// <summary>
/// 悬浮窗（快速启动）。置顶、无任务栏按钮、Esc 隐藏、失焦可配置是否自动隐藏。
/// 窗口本身只负责呈现与键盘/焦点行为，全部数据来自共享视图模型。
/// </summary>
public partial class FloatingWindow : Window
{
    private readonly FloatingWindowViewModel _viewModel;
    private readonly AppController _controller;

    public FloatingWindow(FloatingWindowViewModel viewModel, AppController controller)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        DataContext = viewModel;
        InitializeComponent();

        // 失焦自动隐藏：呼出式窗口的常规行为，可在设置里关闭
        Deactivated += OnDeactivated;
        _viewModel.HideRequested += (_, _) => _controller.HideFloating();

        ContentRendered += (_, _) => _ = _controller.EnsureInitializedAsync();
    }

    /// <summary>显示并激活窗口（快捷键呼出）。快捷键属于用户显式操作，允许强制前台。</summary>
    public void ShowAndActivate(bool forceForeground = true)
    {
        Show();
        WindowActivator.Activate(this, forceForeground);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _controller.HideFloating();
            e.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_viewModel.HideOnDeactivate)
        {
            _controller.HideFloating();
        }
    }
}
