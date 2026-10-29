using System;
using System.IO;

namespace DeskLanes.App.Services;

/// <summary>
/// 配置目录的一次性迁移（项目由 DeskSpace 更名为 DeskLanes 引入）。
/// </summary>
/// <remarks>
/// 设计约束：
/// <list type="bullet">
/// <item>只在「新目录不存在」且「旧目录存在」时搬一次，重复启动不会覆盖新配置。</item>
/// <item>用复制而不是移动：<b>旧目录原样保留</b>，任何时候都能手动回退。</item>
/// <item>失败一律返回 false，绝不抛异常、绝不阻塞启动——迁移是锦上添花，不是启动前置条件。</item>
/// </list>
/// </remarks>
public static class ConfigDirectoryMigrator
{
    /// <summary>需要一并搬迁的子目录（日志、配置备份）。</summary>
    private static readonly string[] SubDirectories = { "logs", "backups" };

    /// <summary>
    /// 尝试把旧应用名下的配置目录复制到新目录。
    /// </summary>
    /// <param name="newDir">新配置目录（如 %LocalAppData%\DeskLanes）。</param>
    /// <param name="oldAppName">旧应用名（如 DeskSpace）。</param>
    /// <returns>确实发生了迁移返回 true；无需迁移或迁移失败返回 false。</returns>
    public static bool TryMigrate(string newDir, string oldAppName)
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return TryMigrateFrom(newDir, Path.Combine(root, oldAppName));
    }

    /// <summary>
    /// 从指定的旧目录迁移到新目录（可测试入口：<paramref name="oldDir"/> 直接给完整路径）。
    /// </summary>
    /// <param name="newDir">新配置目录。</param>
    /// <param name="oldDir">旧配置目录完整路径。</param>
    /// <returns>确实发生了迁移返回 true；无需迁移或迁移失败返回 false。</returns>
    public static bool TryMigrateFrom(string newDir, string oldDir)
    {
        try
        {
            // 没有旧配置（全新安装）或新目录已存在（已迁移过 / 已是新配置）→ 什么都不做
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir))
            {
                return false;
            }

            Directory.CreateDirectory(newDir);

            foreach (string file in Directory.GetFiles(oldDir))
            {
                string target = Path.Combine(newDir, Path.GetFileName(file));
                // overwrite: false —— 万一并发启动，也不覆盖已存在的文件
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                }
            }

            foreach (string sub in SubDirectories)
            {
                string srcSub = Path.Combine(oldDir, sub);
                if (!Directory.Exists(srcSub))
                {
                    continue;
                }

                string dstSub = Path.Combine(newDir, sub);
                Directory.CreateDirectory(dstSub);
                foreach (string file in Directory.GetFiles(srcSub))
                {
                    string target = Path.Combine(dstSub, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        File.Copy(file, target);
                    }
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
