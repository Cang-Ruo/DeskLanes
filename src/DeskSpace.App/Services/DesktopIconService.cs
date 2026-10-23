using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DeskSpace.Core.Services;

namespace DeskSpace.App.Services;

/// <summary>
/// 桌面图标隐藏 / 恢复。
/// </summary>
/// <remarks>
/// 实现方式（本机 Win11 26H2 实测有效，见 docs/stage5-evidence 的 p2-* 截图对比）：
///   写入 HKCU\...\Explorer\Advanced\HideIcons（与系统右键"查看 → 显示桌面图标"写的是同一个值），
///   再向所有顶层窗口广播 WM_SETTINGCHANGE，让 Explorer 立即生效——不需要重启 Explorer。
///   只写注册表而不广播是无效的（stage5-probe v1 已验证：截图前后完全一致）。
///
/// 安全红线（与项目约定一致）：
///   - 只写这一个值，不碰其他注册表项；不动桌面上的任何文件；不重启 Explorer；
///   - 默认关闭，且每次启用都要用户明确确认（窗口里走 Confirm 授权）；
///   - 恢复时写回 0；注册表不可访问时返回失败原因，不静默吞掉。
/// </remarks>
public interface IDesktopIconService
{
    /// <summary>当前桌面图标是否处于隐藏状态（按注册表实际值判断）。</summary>
    bool IsHidden { get; }

    /// <summary>读取状态时的说明（成功为 null）。</summary>
    string? StatusText { get; }

    /// <summary>隐藏或恢复桌面图标。</summary>
    OperationResult SetHidden(bool hidden);
}

public sealed class DesktopIconService : IDesktopIconService
{
    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "HideIcons";

    public bool IsHidden { get; private set; }

    public string? StatusText { get; private set; }

    public DesktopIconService()
    {
        Refresh();
    }

    public void Refresh()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath, writable: false);
            object? value = key?.GetValue(ValueName);
            IsHidden = value is int i ? i != 0 : value is not null && Convert.ToInt32(value) != 0;
            StatusText = null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            IsHidden = false;
            StatusText = $"无法读取桌面图标设置：{ex.Message}";
        }
    }

    public OperationResult SetHidden(bool hidden)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(AdvancedKeyPath);
            key.SetValue(ValueName, hidden ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Security.SecurityException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return OperationResult.Fail($"设置桌面图标失败：{ex.Message}");
        }

        // 广播设置变更：这一步是生效的关键（不广播则 Explorer 不会重绘，等于没改）
        NativeMethods.BroadcastSettingChange();

        IsHidden = hidden;
        StatusText = null;
        return OperationResult.Ok();
    }

    private static class NativeMethods
    {
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;
        private static readonly IntPtr HwndBroadcast = new(0xFFFF);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, string? lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        /// <summary>通知所有顶层窗口"设置已变更"，让 Explorer 立即应用而不重启。</summary>
        public static void BroadcastSettingChange()
        {
            // 失败也允许继续：注册表值已写入，下次登录/资源管理器刷新时仍会生效
            SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 2000, out _);
        }
    }
}
