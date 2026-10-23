using DeskSpace.Core.Infrastructure;
using DeskSpace.Core.Models;
using DeskSpace.Core.Services;

namespace DeskSpace.App.ViewModels;

/// <summary>
/// 窗口模式状态管理：记录当前模式与各窗口的可见性，负责持久化与广播。
/// 不持有任何项目数据副本——三种模式的数据都来自同一个 <see cref="IWorkspaceService"/>。
/// 具体窗口的显示/隐藏由 App 层订阅 <see cref="ModeChanged"/> / <see cref="FloatingVisibilityChanged"/> 执行。
/// </summary>
public sealed class WindowModeManager : ObservableObject
{
    private readonly IWorkspaceService _workspace;

    private WindowMode _currentMode;
    private bool _isFloatingVisible;
    private bool _isSidebarVisible;

    public WindowModeManager(IWorkspaceService workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _currentMode = _workspace.Settings.WindowMode;
        _isFloatingVisible = _currentMode == WindowMode.Floating;
        _isSidebarVisible = _currentMode == WindowMode.Sidebar;
    }

    /// <summary>当前窗口模式。</summary>
    public WindowMode CurrentMode
    {
        get => _currentMode;
        private set => SetProperty(ref _currentMode, value);
    }

    /// <summary>悬浮窗当前是否可见（快捷键呼出/隐藏的就是这个状态）。</summary>
    public bool IsFloatingVisible
    {
        get => _isFloatingVisible;
        private set
        {
            if (SetProperty(ref _isFloatingVisible, value))
            {
                OnPropertyChanged(nameof(IsFloatingHidden));
            }
        }
    }

    public bool IsFloatingHidden => !IsFloatingVisible;

    /// <summary>侧边栏当前是否可见。</summary>
    public bool IsSidebarVisible
    {
        get => _isSidebarVisible;
        private set => SetProperty(ref _isSidebarVisible, value);
    }

    /// <summary>模式切换后触发（参数为新的模式）。</summary>
    public event EventHandler<WindowMode>? ModeChanged;

    /// <summary>悬浮窗可见性变化后触发（快捷键呼出/隐藏、Esc 隐藏）。</summary>
    public event EventHandler? FloatingVisibilityChanged;

    /// <summary>切换到指定模式并持久化。</summary>
    public void SwitchTo(WindowMode mode)
    {
        if (mode == CurrentMode && (mode != WindowMode.Floating || IsFloatingVisible))
        {
            // 同模式仍要保证窗口可见（例如悬浮窗被隐藏时再次切到悬浮窗）
            if (mode == WindowMode.Floating)
            {
                ShowFloating();
                return;
            }

            // 已经在目标模式：仍然广播一次模式变更，让窗口被带到前台。
            // 场景：工作空间被别的窗口盖住、或最小化到托盘后，用户在托盘菜单里
            // 又点了一次当前模式——期望的是"把它叫到前面来"，而不是毫无反应。
            ModeChanged?.Invoke(this, mode);
            return;
        }

        CurrentMode = mode;
        _workspace.Settings.WindowMode = mode;
        _workspace.Save();

        switch (mode)
        {
            case WindowMode.Floating:
                IsSidebarVisible = false;
                IsFloatingVisible = true;
                break;
            case WindowMode.Sidebar:
                IsFloatingVisible = false;
                IsSidebarVisible = true;
                break;
            default:
                IsFloatingVisible = false;
                IsSidebarVisible = false;
                break;
        }

        ModeChanged?.Invoke(this, mode);
        if (mode == WindowMode.Floating)
        {
            FloatingVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>显示悬浮窗（快捷键呼出）。</summary>
    public void ShowFloating()
    {
        bool modeChanged = CurrentMode != WindowMode.Floating;
        if (modeChanged)
        {
            CurrentMode = WindowMode.Floating;
            _workspace.Settings.WindowMode = WindowMode.Floating;
            _workspace.Save();
        }

        IsSidebarVisible = false;
        IsFloatingVisible = true;
        ModeChanged?.Invoke(this, WindowMode.Floating);
        FloatingVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>隐藏悬浮窗（快捷键 / Esc）。应用不退出，只是窗口不可见。</summary>
    public void HideFloating()
    {
        if (!IsFloatingVisible)
        {
            return;
        }

        IsFloatingVisible = false;
        FloatingVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>快捷键动作：呼出或隐藏悬浮窗。</summary>
    public void ToggleFloating()
    {
        if (IsFloatingVisible)
        {
            HideFloating();
        }
        else
        {
            ShowFloating();
        }
    }

    /// <summary>隐藏侧边栏（不退出应用）。</summary>
    public void HideSidebar()
    {
        if (!IsSidebarVisible)
        {
            return;
        }

        IsSidebarVisible = false;
    }
}
