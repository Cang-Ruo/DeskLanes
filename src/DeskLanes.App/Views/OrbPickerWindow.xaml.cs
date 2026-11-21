using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeskLanes.App.Views;

/// <summary>
/// 阶段 46：悬浮球单击弹出的一行式分类选择窗。
/// 条目来自悬浮窗共享的分类数据（全部 + 各分组），选择后交由外壳展开悬浮窗并定位。
/// </summary>
public partial class OrbPickerWindow : Window
{
    public sealed class Entry
    {
        public Entry(string name, int count, string? groupId)
        {
            Name = name;
            Count = count.ToString();
            GroupId = groupId;
        }

        public string Name { get; }
        public string Count { get; }
        public string? GroupId { get; }
    }

    public ObservableCollection<Entry> Entries { get; } = new();

    /// <summary>用户选中某分类；group_id 为 null 表示「全部」。</summary>
    public event EventHandler<string?>? CategoryPicked;

    public OrbPickerWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private bool _suppressPick;

    public void SetEntries(System.Collections.Generic.IEnumerable<ViewModels.FloatingCategoryViewModel> categories)
    {
        // 填充列表时抑制 SelectionChanged，否则 SetItems 会误触发选中第一行 → 直接展开
        _suppressPick = true;
        CategoryList.SelectedItem = null;
        Entries.Clear();
        foreach (var c in categories)
        {
            Entries.Add(new Entry(c.Name, c.ItemCount, c.GroupId));
        }

        CategoryList.SelectedItem = null;
        _suppressPick = false;
    }

    /// <summary>把选择窗摆到小球右侧，并按实际高度垂直中心对齐球的中心（DIP）。</summary>
    public void PlaceBesideBall(double leftDip, double ballCenterYDip)
    {
        Left = leftDip;
        _ballCenterYDip = ballCenterYDip;
        // 复用窗口时 ActualHeight 已有值，直接对齐；首次为 0，Loaded 时再校正
        double h = ActualHeight > 0 ? ActualHeight : 200;
        Top = Math.Max(8, ballCenterYDip - h / 2);
        Loaded -= OnLoadedAlign;
        Loaded += OnLoadedAlign;
    }

    private double _ballCenterYDip;

    private void OnLoadedAlign(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedAlign;
        Top = Math.Max(8, _ballCenterYDip - ActualHeight / 2);
    }

    private void Pick(Entry? entry)
    {
        if (entry is null)
        {
            return;
        }

        CategoryPicked?.Invoke(this, entry.GroupId);
        Hide();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPick)
        {
            return;
        }

        if (CategoryList.SelectedItem is Entry entry)
        {
            Pick(entry);
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    /// <summary>供外壳在重新点击小球时复位选择状态。</summary>
    public void ResetSelection()
    {
        _suppressPick = true;
        CategoryList.SelectedItem = null;
        _suppressPick = false;
    }
}
