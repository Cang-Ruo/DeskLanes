using System;

namespace DeskLanes.App.Services;

/// <summary>
/// 全局拖拽状态：项目开始拖拽时置位，结束（放下或取消）时清除。
/// </summary>
/// <remarks>
/// 存在的理由：底部「移除区」如果一直显示，一条红色横条会一直在视觉上抢戏，
/// 与「低存在感」的设计目标冲突。所以它只在真正拖拽项目时才出现。
/// <para>
/// 用引用计数而不是布尔量：拖拽可能嵌套触发（例如从一个分区拖到另一个分区时，
/// 沿途的 DragOver / DragLeave 会多次进出），布尔量容易出现"提前熄灭"。
/// </para>
/// </remarks>
public static class DragState
{
    private static int _refCount;

    /// <summary>状态变化通知（视图模型订阅后转成属性变更通知）。</summary>
    public static event EventHandler? Changed;

    /// <summary>当前是否有项目正在被拖拽。</summary>
    public static bool IsDragging => _refCount > 0;

    /// <summary>开始一次拖拽。与 <see cref="EndDrag"/> 成对调用。</summary>
    public static void BeginDrag()
    {
        _refCount++;
        Notify();
    }

    /// <summary>结束一次拖拽。</summary>
    public static void EndDrag()
    {
        if (_refCount > 0)
        {
            _refCount--;
        }

        Notify();
    }

    /// <summary>强制复位（例如窗口切换、异常收尾时兜底）。</summary>
    public static void Reset()
    {
        _refCount = 0;
        Notify();
    }

    private static void Notify() => Changed?.Invoke(null, EventArgs.Empty);
}
