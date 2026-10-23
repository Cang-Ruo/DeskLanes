using System.IO;
using DeskSpace.Core.Models;

namespace DeskSpace.Core.Services;

/// <summary>
/// 从磁盘路径创建 <see cref="DesktopItem"/>（事实信息：类型/显示名/快捷方式解析）。
/// 扫描器与自定义添加共用，保证两处产出的项目形状一致。
/// </summary>
public static class DesktopItemFactory
{
    public static DesktopItem Create(string fullPath, ItemOrigin origin, IShortcutResolver shortcutResolver)
    {
        var item = new DesktopItem
        {
            FullPath = fullPath,
            Origin = origin,
            AddedAt = DateTime.Now,
            LastSeenAt = DateTime.Now,
        };

        if (Directory.Exists(fullPath))
        {
            item.Kind = DesktopItemKind.Folder;
            item.Name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        }
        else
        {
            string extension = Path.GetExtension(fullPath);
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                item.Kind = DesktopItemKind.Shortcut;
                item.Name = Path.GetFileNameWithoutExtension(fullPath);

                if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    item.ShortcutTarget = UrlShortcutReader.TryRead(fullPath, out string? urlError);
                    item.ShortcutTargetExists = item.ShortcutTarget is null ? null : true;
                    item.ShortcutError = urlError;
                }
                else
                {
                    ShortcutInfo? info = shortcutResolver.Resolve(fullPath);
                    if (info is null)
                    {
                        item.ShortcutError = "无法解析此快捷方式";
                    }
                    else
                    {
                        item.ShortcutTarget = info.Target;
                        item.ShortcutTargetExists = info.Target is null ? null : info.TargetExists;
                        item.ShortcutError = info.Error;
                    }
                }
            }
            else
            {
                item.Kind = DesktopItemKind.File;
                item.Name = Path.GetFileNameWithoutExtension(fullPath);
            }
        }

        if (string.IsNullOrEmpty(item.Name))
        {
            item.Name = Path.GetFileName(fullPath);
        }

        return item;
    }
}
