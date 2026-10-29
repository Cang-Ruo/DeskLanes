using System.Windows;
using DeskLanes.App.Services;

namespace DeskLanes.App.Views;

/// <summary>单行文本输入对话框（分类/分区命名等）。</summary>
public partial class TextInputDialog : Window
{
    /// <summary>用户输入的文本；取消时为 null。</summary>
    public string? Value { get; private set; }

    public TextInputDialog(string title, string message, string initialValue = "")
    {
        InitializeComponent();
        // 与 MainWindow / 设置窗口同一套轻玻璃背景（分层窗口由 XAML 自己处理）
        WindowBackdropService.Attach(this);
        Title = title;
        MessageText.Text = message;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Value = InputBox.Text;
        DialogResult = true;
    }
}
