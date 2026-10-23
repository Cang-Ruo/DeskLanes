using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DeskSpace.Core.Models;

namespace DeskSpace.Core.Services;

/// <summary>
/// 用 Windows Shell 机制启动项目：
/// 文件/快捷方式经 ShellExecute（等价于双击，保留系统默认关联与快捷方式自身语义）；
/// 文件夹经资源管理器打开。启动前做存在性校验，失败时返回可读的中文提示。
/// </summary>
public sealed class LaunchService : ILaunchService
{
    public LaunchResult Launch(DesktopItem item)
    {
        try
        {
            bool exists = item.Kind == DesktopItemKind.Folder
                ? Directory.Exists(item.FullPath)
                : File.Exists(item.FullPath);

            if (!exists)
            {
                return LaunchResult.Fail($"项目已不存在或路径无效：\n{item.FullPath}");
            }

            if (item.Kind == DesktopItemKind.Folder)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.FullPath}\"")
                {
                    UseShellExecute = true,
                });
                return LaunchResult.Ok();
            }

            if (item.IsBroken)
            {
                return LaunchResult.Fail(
                    $"快捷方式的目标已失效，无法打开：\n{item.ShortcutTarget ?? item.FullPath}");
            }

            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
            return LaunchResult.Ok();
        }
        catch (Win32Exception ex)
        {
            string reason = ex.NativeErrorCode switch
            {
                1155 => "没有找到可以打开该文件的关联程序",
                1223 => "操作已被取消（可能被 UAC 或安全软件拦截）",
                5 => "权限不足，无法启动该项目",
                _ => ex.Message,
            };
            return LaunchResult.Fail($"{reason}\n{item.FullPath}");
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail($"启动失败：{ex.Message}\n{item.FullPath}");
        }
    }
}
