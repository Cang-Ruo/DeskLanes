using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using DeskSpace.Core.Services;

namespace DeskSpace.App.Services;

/// <summary>
/// 开机自启：只维护当前用户 Run 启动项里"自己这一条"。
/// </summary>
/// <remarks>
/// 安全红线（与项目约定一致）：
///   - 只写 HKCU（当前用户），不碰 HKLM，不需要管理员权限；
///   - 只创建/删除名为 DeskSpace 的这一个值，不读写其他程序的启动项；
///   - 默认关闭，只有用户在设置里主动勾选才写入（不在首次运行时替用户决定）；
///   - 注册表不可访问时返回失败原因，绝不静默吞掉也不强制继续。
/// </remarks>
public interface IStartupService
{
    /// <summary>当前是否已设置为开机自启。</summary>
    bool IsEnabled { get; }

    /// <summary>读取启动项时的人可读说明（成功为 null，失败给出原因）。</summary>
    string? StatusText { get; }

    /// <summary>开机自启对应的命令行（供提示与排错）。</summary>
    string CommandLine { get; }

    /// <summary>重新读取启动项状态。用户或第三方工具可能在软件运行期间改过。</summary>
    void Refresh();

    /// <summary>启用或停用开机自启。</summary>
    OperationResult SetEnabled(bool enabled);
}

public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskSpace";

    public bool IsEnabled { get; private set; }

    public string? StatusText { get; private set; }

    public string CommandLine => $"\"{ExecutablePath}\"";

    /// <summary>当前可执行文件完整路径（自启项要指向它）。</summary>
    private static string ExecutablePath
        => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public StartupService()
    {
        Refresh();
    }

    /// <summary>重新读取启动项状态。</summary>
    public void Refresh()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null)
            {
                // 键不存在等价于"没有启用"
                IsEnabled = false;
                StatusText = null;
                return;
            }

            object? value = key.GetValue(ValueName);
            IsEnabled = value is not null;
            StatusText = null;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            IsEnabled = false;
            StatusText = $"无法读取开机启动项：{ex.Message}";
        }
    }

    public OperationResult SetEnabled(bool enabled)
    {
        string exePath = ExecutablePath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return OperationResult.Fail("无法获取当前程序路径，开机自启未设置。");
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                // 值不存在时 DeleteValue 会抛异常，先判断再删
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return OperationResult.Fail($"设置开机自启失败：{ex.Message}");
        }

        IsEnabled = enabled;
        StatusText = null;
        return OperationResult.Ok();
    }
}
