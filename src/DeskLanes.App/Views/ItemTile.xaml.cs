using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskLanes.App.Services;
using DeskLanes.Core.Models;

namespace DeskLanes.App.Views;

/// <summary>
/// 项目磁贴（图标 + 名称 + 状态徽标）。支持：
/// <list type="bullet">
/// <item>按住拖动：把项目拖到分类、分区、项目池或移除区（拖拽 Id 由 <see cref="DragDropFormats"/> 承载）。</item>
/// <item>双击打开：磁贴自己处理，<b>直接以自身 DataContext 为参数</b>。</item>
/// </list>
/// <para>
/// 阶段 14 修复：原先双击由外层 ListBox 的 InputBindings 处理，参数绑的是
/// <c>SelectedItem</c>。磁贴的拖拽预览会先吃掉按下事件，选中态经常没同步，
/// 于是命令的 CanExecute 拿不到 DesktopItem、双击没反应，只有右键菜单
/// （参数取 PlacementTarget.DataContext）能打开。现在统一由磁贴直接执行。
/// </para>
/// </summary>
public partial class ItemTile : UserControl
{
    private Point _startPoint;
    private bool _pendingDrag;

    public ItemTile()
    {
        InitializeComponent();

        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseDoubleClick += OnDoubleClick;
    }

    private DesktopItem? Item => DataContext as DesktopItem;

    /// <summary>沿可视树找带 OpenItemCommand 的宿主并执行；PanelView 用依赖属性暴露命令。</summary>
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DesktopItem? item = Item;
        if (item is null)
        {
            return;
        }

        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null)
        {
            ICommand? command = null;
            if (node is PanelView panel)
            {
                command = panel.OpenItemCommand;
            }
            else if (node is FrameworkElement element && element.DataContext is not null)
            {
                // 各宿主 VM 的 OpenItemCommand 都是公开属性；用反射避免为
                // 四个 ViewModel 引入同一个接口、改动签名。
                command = element.DataContext.GetType()
                    .GetProperty("OpenItemCommand")?.GetValue(element.DataContext) as ICommand;
            }

            if (command is not null && command.CanExecute(item))
            {
                command.Execute(item);
                e.Handled = true;
                return;
            }

            node = VisualTreeHelper.GetParent(node);
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        _pendingDrag = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_pendingDrag || Item is null)
        {
            return;
        }

        // 超过系统最小拖动距离才开始拖拽，避免普通点击误触发
        Point position = e.GetPosition(this);
        bool exceededX = Math.Abs(position.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance;
        bool exceededY = Math.Abs(position.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance;
        if (!exceededX && !exceededY)
        {
            return;
        }

        _pendingDrag = false;
        try
        {
            // 通知全局：此刻有项目在拖。底部移除区据此显示。
            DragState.BeginDrag();
            DragDrop.DoDragDrop(this, DragDropFormats.CreateDataObject(Item.Id), DragDropEffects.Move);
        }
        catch
        {
            // 拖拽异常（极少数 COM 场景）不应影响应用
        }
        finally
        {
            // DoDragDrop 是同步阻塞的，返回即代表拖拽结束
            DragState.EndDrag();
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => _pendingDrag = false;
}
