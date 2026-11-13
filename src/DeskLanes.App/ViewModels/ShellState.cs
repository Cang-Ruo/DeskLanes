using DeskLanes.Core.Infrastructure;

namespace DeskLanes.App.ViewModels;

/// <summary>
/// 跨布局模式共享的状态（当前搜索词）。分类导航式与自由分区式共用同一个实例，
/// 切换布局时搜索词不丢失。
/// </summary>
public sealed class ShellState : ObservableObject
{
    private string _searchText = string.Empty;

    /// <summary>当前搜索词。为空表示不搜索。</summary>
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    /// <summary>是否有有效搜索词（去除空白后非空）。</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>
    /// 阶段 31：任一视图模型成功启动了一个项目（双击磁贴 / 侧栏 / 悬浮窗）。
    /// AppController 订阅它把全屏或侧边栏「收束」成左侧竖线。
    /// 只有启动成功才发；启动失败会弹错误提示，此时不收束，方便继续操作。
    /// </summary>
    public event EventHandler? ItemLaunched;

    public void NotifyItemLaunched() => ItemLaunched?.Invoke(this, EventArgs.Empty);
}
