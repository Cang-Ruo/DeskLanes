using System.Windows;

namespace DeskLanes.App.Views;

/// <summary>
/// 绑定代理：把视图的 DataContext 暴露给 ContextMenu 等不在可视化树中的元素
/// （ContextMenu 的 RelativeSource FindAncestor 不可用，需借此中转）。
/// </summary>
public sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));
}
