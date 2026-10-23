using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskSpace.Core;
using DeskSpace.Core.Models;

namespace DeskSpace.App.Services;

/// <summary>托盘菜单项。</summary>
public sealed class TrayMenuItem
{
    public TrayMenuItem(string text, int id, bool isSeparator = false)
    {
        Text = text;
        Id = id;
        IsSeparator = isSeparator;
    }

    public string Text { get; }

    public int Id { get; }

    public bool IsSeparator { get; }

    public static TrayMenuItem Separator() => new("-", -1, true);
}

/// <summary>
/// 系统托盘服务（自实现，纯 P/Invoke，不引用 WinForms）。
/// <para>
/// 职责：注册/更新/删除托盘图标、显示原生弹出菜单、气球提示、
/// 以及在 Explorer 重启（TaskbarCreated）后自动重新注册。
/// 菜单点击与图标双击通过事件交给上层处理，服务本身不关心业务。
/// </para>
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int TrayIconId = 0x5201;

    private readonly IntPtr _windowHandle;
    private readonly List<TrayMenuItem> _menuItems = new();
    private readonly IntPtr _icon;
    private bool _added;
    private bool _disposed;
    private int _taskbarCreatedMessageId;

    public TrayIconService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _icon = LoadIcon();
        _taskbarCreatedMessageId = WindowInterop.RegisterMessage("TaskbarCreated");

        Add();
    }

    /// <summary>托盘提示文字（最长 127 字符）。</summary>
    public string ToolTipText { get; set; } = "DeskSpace";

    /// <summary>菜单项（调用方在注册前设置好；点击时按 Id 回调）。</summary>
    public IReadOnlyList<TrayMenuItem> MenuItems
    {
        get => _menuItems;
        set
        {
            _menuItems.Clear();
            _menuItems.AddRange(value);
        }
    }

    /// <summary>菜单项被点击（参数为菜单 Id）。</summary>
    public event EventHandler<int>? MenuCommand;

    /// <summary>图标被双击（或用键盘选择）。</summary>
    public event EventHandler? Activated;

    /// <summary>注册托盘图标。</summary>
    public void Add()
    {
        if (_disposed || _added)
        {
            return;
        }

        var data = CreateData(TrayInterop.NimAdd);
        if (TrayInterop.Shell_NotifyIcon(TrayInterop.NimAdd, ref data))
        {
            _added = true;
        }
    }

    /// <summary>更新提示文字。</summary>
    public void UpdateTip()
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData(TrayInterop.NimModify);
        data.uFlags = TrayInterop.NifTip;
        data.szTip = Truncate(ToolTipText, 127);
        TrayInterop.Shell_NotifyIcon(TrayInterop.NimModify, ref data);
    }

    /// <summary>显示气球提示。</summary>
    public void ShowBalloon(string title, string message, TrayBalloonIcon icon = TrayBalloonIcon.Warning)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData(TrayInterop.NimModify);
        data.uFlags = TrayInterop.NifInfo;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message.Replace("\n", " "), 255);
        data.dwInfoFlags = icon switch
        {
            TrayBalloonIcon.Error => TrayInterop.NiifError,
            TrayBalloonIcon.Info => TrayInterop.NiifInfo,
            _ => TrayInterop.NiifWarning,
        };
        TrayInterop.Shell_NotifyIcon(TrayInterop.NimModify, ref data);
    }

    /// <summary>删除托盘图标（退出时调用）。</summary>
    public void Remove()
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData(TrayInterop.NimDelete);
        TrayInterop.Shell_NotifyIcon(TrayInterop.NimDelete, ref data);
        _added = false;
    }

    /// <summary>
    /// 处理承载窗口的回调消息。返回 true 表示已处理。
    /// </summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_disposed)
        {
            return false;
        }

        if (msg == _taskbarCreatedMessageId)
        {
            // Explorer 重启：托盘图标会丢失，重新注册
            _added = false;
            Add();
            UpdateTip();
            return true;
        }

        if (msg != TrayInterop.TrayCallbackMessage || wParam.ToInt32() != TrayIconId)
        {
            return false;
        }

        int mouseMessage = lParam.ToInt32();
        switch (mouseMessage)
        {
            case TrayInterop.WmLButtonDblClk:
            case TrayInterop.NinSelect:
                Activated?.Invoke(this, EventArgs.Empty);
                return true;

            case TrayInterop.WmRButtonUp:
                ShowContextMenu();
                return true;

            default:
                return false;
        }
    }

    /// <summary>处理承载窗口的 WM_COMMAND（原生菜单点击）。</summary>
    public bool HandleCommand(IntPtr wParam)
    {
        int id = wParam.ToInt32() & 0xFFFF;
        if (id <= 0)
        {
            return false;
        }

        MenuCommand?.Invoke(this, id);
        return true;
    }

    private void ShowContextMenu()
    {
        if (_menuItems.Count == 0)
        {
            return;
        }

        IntPtr menu = TrayInterop.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach (TrayMenuItem item in _menuItems)
            {
                if (item.IsSeparator)
                {
                    TrayInterop.AppendMenu(menu, TrayInterop.MfSeparator, 0, string.Empty);
                }
                else
                {
                    TrayInterop.AppendMenu(menu, TrayInterop.MfString, item.Id, item.Text);
                }
            }

            // TrackPopupMenu 的官方要求：调用前必须让自己的窗口成为前台窗口。
            // 否则弹出的菜单拿不到键盘焦点（方向键会直接把菜单关掉），
            // 点击菜单外部时也可能不消失。托盘菜单由 Explorer 转发消息而来，
            // 此时桌面 / 资源管理器通常是前台窗口，所以这里必须主动切前台。
            TrayInterop.SetForegroundWindow(_windowHandle);

            TrayInterop.POINT cursor = TrayInterop.GetCursorPosition();

            // TPM_RETURNCMD 让 TrackPopupMenu 同步返回被点击的菜单 Id
            int flags = TrayInterop.TpmLeftAlign | TrayInterop.TpmRightButton |
                        TrayInterop.TpmRightAlign | TrayInterop.TpmReturnCmd;

            // 右对齐：让菜单右边缘对齐光标，符合托盘图标的使用习惯
            int command = TrayInterop.TrackPopupMenu(
                menu, flags, cursor.X, cursor.Y, 0, _windowHandle, IntPtr.Zero);

            // 菜单关闭再补发一次 WM_NULL 触发任务切换：这是官方文档针对
            // 「第二次弹出托盘菜单时菜单一闪即消失」的已知问题的推荐做法。
            TrayInterop.PostMessage(_windowHandle, TrayInterop.WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command > 0)
            {
                MenuCommand?.Invoke(this, command);
            }
        }
        finally
        {
            TrayInterop.DestroyMenu(menu);
        }
    }

    private TrayInterop.NOTIFYICONDATAW CreateData(int message)
    {
        var data = new TrayInterop.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<TrayInterop.NOTIFYICONDATAW>(),
            hWnd = _windowHandle,
            uID = TrayIconId,
            uFlags = TrayInterop.NifMessage | TrayInterop.NifIcon | TrayInterop.NifTip,
            uCallbackMessage = TrayInterop.TrayCallbackMessage,
            hIcon = _icon,
            szTip = Truncate(ToolTipText, 127),
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            dwInfoFlags = 0,
        };

        _ = message;
        return data;
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max]);

    /// <summary>优先用应用自身的图标，失败时用系统默认应用图标。</summary>
    private static IntPtr LoadIcon()
    {
        try
        {
            // 取当前可执行文件真实路径：单文件发布时 Assembly.Location 恒为空（IL3000），
            // 用 Environment.ProcessPath（单文件下也有效），再退回 AppContext.BaseDirectory。
            string? location = Environment.ProcessPath;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                string candidate = Path.Combine(AppContext.BaseDirectory, "DeskSpace.exe");
                location = File.Exists(candidate) ? candidate : null;
            }

            if (!string.IsNullOrEmpty(location) &&
                TrayInterop.ExtractIconEx(location, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero)
                    {
                        TrayInterop.DestroyIcon(large);
                    }

                    return small;
                }

                if (large != IntPtr.Zero)
                {
                    return large;
                }
            }
        }
        catch
        {
            // 落到系统默认图标
        }

        return TrayInterop.LoadIcon(IntPtr.Zero, new IntPtr(TrayInterop.IdiApplication));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Remove();

        try
        {
            TrayInterop.DestroyIcon(_icon);
        }
        catch
        {
            // 忽略
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>气球提示图标类型。</summary>
public enum TrayBalloonIcon
{
    Info,

    Warning,

    Error,
}
