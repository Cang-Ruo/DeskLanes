using System.Windows;
using DeskSpace.App.Services;
using DeskSpace.App.ViewModels;

namespace DeskSpace.App.Views;

/// <summary>设置窗口（窗口模式 / 快捷键 / 悬浮窗 / 侧边栏 / 托盘行为）。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        // 与 MainWindow 同一套轻玻璃背景（Mica 优先，失败自动回退不透明底色）
        WindowBackdropService.Attach(this);

        viewModel.Saved += (_, _) => DialogResult = true;
        viewModel.Cancelled += (_, _) => DialogResult = false;
    }
}
