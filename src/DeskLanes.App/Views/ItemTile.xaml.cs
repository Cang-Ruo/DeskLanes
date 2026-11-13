using System.Windows;
using DeskLanes.Core;
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
    /// <summary>
    /// 阶段 31：全屏模式下把整块磁贴等比缩到 80%（悬浮窗保持 1.0）。
    /// 用 LayoutTransform 而不是改 Width/Height：父容器的 WrapPanel / 自适应分区
    /// 会按变换后的尺寸排版，图标、名字牌、文字一起等比缩小，不会错位。
    /// 图标本身已有 BitmapScalingMode=HighQuality，缩小后更锐利。
    /// </summary>
    public static readonly DependencyProperty TileScaleProperty =
        DependencyProperty.Register(nameof(TileScale), typeof(double), typeof(ItemTile),
            new PropertyMetadata(1.0, OnTileScaleChanged));

    public double TileScale
    {
        get => (double)GetValue(TileScaleProperty);
        set => SetValue(TileScaleProperty, value);
    }

    private static void OnTileScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ItemTile tile && e.NewValue is double scale && scale > 0.1 && scale <= 2)
        {
            tile.LayoutTransform = new ScaleTransform(scale, scale);
        }
    }

    private Point _startPoint;
    private bool _pendingDrag;
    // 阶段 24：拖拽时跟随鼠标的软件图标幽灵窗口
    private Window? _dragGhost;

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
            // 通知全局：此刻有项目在拖。底部移除区据此显示，
            // 底部「全部」覆盖层也会暂时淡出让开画布。
            DragState.BeginDrag();

            // 阶段 24：拖起来时跟着一个带软件图标的小窗口，
            // 不再只有系统默认的空白拖拽反馈。
            try
            {
                _dragGhost = CreateDragGhost();
                GiveFeedback += OnGiveFeedback;
            }
            catch
            {
                // 幽灵窗口创建失败（极少数 COM/DWM 场景）不影响拖放本身
                _dragGhost = null;
            }

            DragDrop.DoDragDrop(this, DragDropFormats.CreateDataObject(Item.Id), DragDropEffects.Move);
        }
        catch
        {
            // 拖拽异常（极少数 COM 场景）不应影响应用
        }
        finally
        {
            GiveFeedback -= OnGiveFeedback;
            if (_dragGhost is not null)
            {
                try
                {
                    _dragGhost.Close();
                }
                catch
                {
                    // 关闭失败不影响后续
                }

                _dragGhost = null;
            }

            // DoDragDrop 是同步阻塞的，返回即代表拖拽结束
            DragState.EndDrag();
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => _pendingDrag = false;
    /// <summary>拖动过程中把幽灵窗口挪到鼠标旁（物理像素换算成 DIP）。</summary>
    private void PositionGhost(Window ghost)
    {
        if (!MonitorInterop.GetCursorPos(out MonitorInterop.POINT pt))
        {
            return;
        }

        double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        ghost.Left = pt.X * scale + 16;
        ghost.Top = pt.Y * scale + 14;
    }

    private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_dragGhost is not null)
        {
            PositionGhost(_dragGhost);
        }

        e.Handled = true;
    }

    /// <summary>创建跟随鼠标的拖拽图标（图标 + 名称），鼠标可穿透、不抢焦点。</summary>
    private Window CreateDragGhost()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (Item?.Icon is ImageSource icon)
        {
            row.Children.Add(new Image
            {
                Source = icon,
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = Item?.Name ?? string.Empty,
            Foreground = Brushes.White,
            FontSize = 12,
            MaxWidth = 160,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var ghost = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Opacity = 0.92,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2B, 0x2B, 0x2B)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 10, 5),
                Child = row,
            },
        };

        PositionGhost(ghost);
        ghost.Show();
        return ghost;
    }

}
