using System.Windows;
using DeskSpace.Core.Services;
using DeskSpace.App.Views;
using Microsoft.Win32;

namespace DeskSpace.App.Services;

/// <summary>基于 WPF 标准对话框的提示/输入/选择实现。</summary>
public sealed class WpfDialogService : IDialogService
{
    private Window? _owner;

    /// <summary>设置宿主窗口，保证弹窗居中于主窗口。</summary>
    public void SetOwner(Window owner) => _owner = owner;

    public void ShowError(string message, string title = "DeskSpace")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message, string title = "DeskSpace")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message, string title = "DeskSpace")
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? Prompt(string message, string title = "DeskSpace", string initialValue = "")
    {
        var dialog = new TextInputDialog(title, message, initialValue);
        if (_owner is not null)
        {
            dialog.Owner = _owner;
        }

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public string? PickFile(string title = "选择项目", string filter = "所有文件|*.*")
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title = "选择文件夹")
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
