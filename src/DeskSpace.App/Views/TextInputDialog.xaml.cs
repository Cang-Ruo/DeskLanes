using System.Windows;

namespace DeskSpace.App.Views;

/// <summary>单行文本输入对话框（分类/分区命名等）。</summary>
public partial class TextInputDialog : Window
{
    /// <summary>用户输入的文本；取消时为 null。</summary>
    public string? Value { get; private set; }

    public TextInputDialog(string title, string message, string initialValue = "")
    {
        InitializeComponent();
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
