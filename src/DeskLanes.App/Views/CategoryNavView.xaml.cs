using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskLanes.App.ViewModels;
using DeskLanes.Core.Models;

namespace DeskLanes.App.Views;

/// <summary>
/// 分类导航视图：左侧分类列表（拖放目标），右侧项目网格。
/// 交互逻辑（打开/移除/拖拽归属）都转发给 <see cref="CategoryNavViewModel"/>。
/// </summary>
public partial class CategoryNavView : UserControl
{
    public CategoryNavView()
    {
        InitializeComponent();
    }

    private void OnCategoryDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropFormats.GetItemId(e.Data) is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>把拖拽的项目放入指针下方的分类节点。</summary>
    private void OnCategoryDrop(object sender, DragEventArgs e)
    {
        string? itemId = DragDropFormats.GetItemId(e.Data);
        if (itemId is null || DataContext is not CategoryNavViewModel viewModel)
        {
            return;
        }

        CategoryNodeViewModel? node = ResolveCategoryNode(e.OriginalSource) ?? viewModel.SelectedCategory;
        DesktopItem? item = viewModel.FindItemById(itemId);
        if (node is not null && item is not null)
        {
            viewModel.AssignToNode(item, node);
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
        if (itemId is null || DataContext is not CategoryNavViewModel viewModel)
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

    /// <summary>从事件源向上找到分类列表项，取其 DataContext 作为放置目标。</summary>
    private static CategoryNodeViewModel? ResolveCategoryNode(object? originalSource)
    {
        DependencyObject? current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is ListBoxItem { DataContext: CategoryNodeViewModel node })
            {
                return node;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is FrameworkElement element && element.Parent is DependencyObject parent)
        {
            return parent;
        }

        return System.Windows.Media.VisualTreeHelper.GetParent(current);
    }
}
