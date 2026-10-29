using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>
/// 通过 IShellLinkW 解析 .lnk 快捷方式。只读取，不修改快捷方式文件。
/// </summary>
public sealed class ShortcutResolver : IShortcutResolver
{
    public ShortcutInfo? Resolve(string linkPath)
    {
        try
        {
            var link = (ShellLinkInterop.IShellLinkW)new ShellLinkInterop.ShellLink();
            ((ShellLinkInterop.IPersistFile)link).Load(linkPath, 0); // STGM_READ

            var target = new StringBuilder(260);
            var findData = new ShellLinkInterop.WIN32_FIND_DATA();
            link.GetPath(target, target.Capacity, ref findData, ShellLinkInterop.SLGP_RAWPATH);

            var arguments = new StringBuilder(260);
            link.GetArguments(arguments, arguments.Capacity);

            var workingDirectory = new StringBuilder(260);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);

            var iconLocation = new StringBuilder(260);
            link.GetIconLocation(iconLocation, iconLocation.Capacity, out int iconIndex);

            link.GetShowCmd(out int showCmd);

            string targetPath = target.ToString();
            bool targetExists = targetPath.Length > 0 &&
                                (File.Exists(targetPath) || Directory.Exists(targetPath));

            return new ShortcutInfo
            {
                Target = targetPath.Length > 0 ? targetPath : null,
                Arguments = arguments.ToString(),
                WorkingDirectory = workingDirectory.ToString(),
                IconLocation = iconLocation.ToString(),
                IconIndex = iconIndex,
                ShowCmd = showCmd,
                TargetExists = targetExists,
                Error = targetPath.Length == 0
                    ? "无法通过路径解析目标（可能是 UWP/商店应用，仍可尝试直接打开）"
                    : targetExists
                        ? null
                        : "目标文件或文件夹不存在",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidCastException)
        {
            return new ShortcutInfo { Error = $"快捷方式解析失败：{ex.GetType().Name}" };
        }
    }
}

/// <summary>
/// 读取 .url 网址快捷方式（INI 格式文本，通常为 UTF-8 或 UTF-16LE）。
/// </summary>
internal static class UrlShortcutReader
{
    public static string? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
                : Encoding.UTF8.GetString(bytes);

            foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(4).Trim();
                }
            }

            error = "未能从 .url 文件中读取到 URL";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取 .url 文件失败：{ex.GetType().Name}";
            return null;
        }
    }
}
