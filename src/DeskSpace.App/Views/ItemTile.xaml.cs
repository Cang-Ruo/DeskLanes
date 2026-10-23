using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskSpace.Core.Models;

namespace DeskSpace.App.Views;

/// <summary>
/// 项目磁贴（图标 + 名称 + 状态徽标）。支持：
/// <list type="bullet">
/// <item>按住拖动：把项目拖到分类、分区、项目池或移除区（拖拽 Id 由 <see cref="DragDropFormats"/> 承载）。</item>
/// <item>双击/回车打开：由外层 ListBox 的 InputBindings 处理，磁贴自身不重复响应。</item>
/// </list>
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
    }

    private DesktopItem? Item => DataContext as DesktopItem;

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
            DragDrop.DoDragDrop(this, DragDropFormats.CreateDataObject(Item.Id), DragDropEffects.Move);
        }
        catch
        {
            // 拖拽异常（极少数 COM 场景）不应影响应用
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => _pendingDrag = false;
}
