using System.IO;
using System.Runtime.InteropServices;
using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>
/// 全局快捷键服务：用 RegisterHotKey 注册组合键，供悬浮窗呼出/隐藏。
/// 注册必须传入一个属于当前线程的窗口句柄（调用方负责把 WM_HOTKEY 消息转给 <see cref="RaisePressed"/>）。
/// 服务自身只负责注册、注销、冲突说明与释放，不依赖任何具体窗口。
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>是否已成功注册。</summary>
    bool IsRegistered { get; }

    /// <summary>当前已注册的快捷键；未注册时为 null。</summary>
    HotkeyDefinition? Current { get; }

    /// <summary>快捷键被按下时触发（由消息循环调用方触发）。</summary>
    event EventHandler? Pressed;

    /// <summary>注册指定组合键。已注册时先注销旧的再注册新的。</summary>
    OperationResult Register(HotkeyDefinition definition);

    /// <summary>注销当前快捷键。未注册时返回 true（幂等）。</summary>
    bool Unregister();
}

/// <summary><see cref="IHotkeyService"/> 的默认实现。</summary>
public sealed class HotkeyService : IHotkeyService
{
    /// <summary>本服务使用的热键标识（WM_HOTKEY 的 wParam）。</summary>
    public const int HotkeyId = 0x5101;

    private readonly IntPtr _windowHandle;
    private readonly object _gate = new();
    private HotkeyDefinition? _current;
    private bool _disposed;

    public HotkeyService(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("窗口句柄不能为空。", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
    }

    public bool IsRegistered
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    public HotkeyDefinition? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Pressed;

    public OperationResult Register(HotkeyDefinition definition)
    {
        if (definition is null)
        {
            return OperationResult.Fail("快捷键不能为空。");
        }

        if (!definition.IsValid)
        {
            return OperationResult.Fail(
                "快捷键必须包含至少一个修饰键（Ctrl / Alt / Shift / Win），且不能是单独的 Win 键以外的裸键。");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return OperationResult.Fail("快捷键服务已释放，无法注册。");
            }

            // 切换快捷键前先释放旧的，避免同进程内重复注册冲突
            if (!UnregisterCore())
            {
                return OperationResult.Fail(
                    $"无法释放原快捷键 {_current?.DisplayText}（错误码 {Marshal.GetLastWin32Error()}），新快捷键未设置。");
            }

            if (!HotkeyInterop.RegisterHotKey(
                    _windowHandle,
                    HotkeyId,
                    definition.Modifiers | HotkeyDefinition.ModNoRepeat,
                    definition.Key))
            {
                int error = Marshal.GetLastWin32Error();
                string message = error == HotkeyInterop.ErrorHotkeyAlreadyRegistered
                    ? $"组合键 {definition.DisplayText} 已被其他程序占用。\n请换一个组合键，例如 Ctrl+Alt+D 或 Ctrl+Shift+Space。"
                    : $"注册快捷键 {definition.DisplayText} 失败（错误码 {error}）。\n请尝试更换组合键。";
                return OperationResult.Fail(message);
            }

            _current = definition;
            return OperationResult.Ok();
        }
    }

    public bool Unregister()
    {
        lock (_gate)
        {
            return UnregisterCore();
        }
    }

    private bool UnregisterCore()
    {
        if (_current is null)
        {
            return true;
        }

        bool success = HotkeyInterop.UnregisterHotKey(_windowHandle, HotkeyId);
        if (success)
        {
            // 只有真的注销成功才清空状态；失败时保留 _current，
            // 让"内存状态"和"系统里仍被占用的热键"保持一致，而不是自欺欺人。
            _current = null;
        }

        return success;
    }

    /// <summary>消息循环收到 WM_HOTKEY 时调用（线程安全）。</summary>
    public void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnregisterCore();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 将"已注册"状态的快捷键描述为便于界面展示的文本（含占用提示）。
/// </summary>
public static class HotkeyStatusText
{
    public static string Describe(IHotkeyService service)
        => service.IsRegistered && service.Current is not null
            ? $"全局快捷键：{service.Current.DisplayText}"
            : "全局快捷键：未注册";
}
