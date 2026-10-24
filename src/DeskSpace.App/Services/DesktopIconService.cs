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

    /// <summary>重新读取系统当前状态。用户可能在软件运行期间用 Windows 自己的右键菜单改过。</summary>
    void Refresh();

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
            // 不假设值的类型：Windows 自己写的就是 DWord，但防御性处理其他类型，避免解析异常
            IsHidden = value switch
            {
                int i => i != 0,
                null => false,
                _ => long.TryParse(value.ToString(), out long parsed) && parsed != 0,
            };
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

        // 广播设置变更：这一步是生效的关键（不广播则 Explorer 不会重绘，等于没改）。
        // 广播失败必须如实反馈——否则用户会以为已经生效。
        bool broadcastOk = NativeMethods.BroadcastSettingChange();

        // 回读校验：写入成功不等于状态正确，以注册表实际值为准，不凭"刚写过的值"下结论。
        Refresh();

        if (IsHidden != hidden)
        {
            StatusText = "已写入注册表，但回读结果不一致，桌面图标当前状态未知。";
            return OperationResult.Fail(StatusText);
        }

        if (!broadcastOk)
        {
            StatusText = "已写入注册表，但通知资源管理器失败，桌面可能要等注销或重启资源管理器后才会变化。";
            return OperationResult.Fail(StatusText);
        }

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

        /// <summary>
        /// 通知所有顶层窗口"设置已变更"，让 Explorer 立即应用而不重启。
        /// 返回是否广播成功；失败时由调用方向用户如实反馈
        /// （注册表值已写入，注销或重启资源管理器后仍会生效）。
        /// </summary>
        public static bool BroadcastSettingChange()
        {
            return SendMessageTimeout(
                HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 2000, out _)
                != IntPtr.Zero;
        }
    }
}
