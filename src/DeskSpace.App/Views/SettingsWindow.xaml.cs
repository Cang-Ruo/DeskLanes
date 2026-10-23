using System.Windows;
using DeskSpace.App.ViewModels;

namespace DeskSpace.App.Views;

/// <summary>设置窗口（窗口模式 / 快捷键 / 悬浮窗 / 侧边栏 / 托盘行为）。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        viewModel.Saved += (_, _) => DialogResult = true;
        viewModel.Cancelled += (_, _) => DialogResult = false;
    }
}
