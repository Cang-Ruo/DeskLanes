using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DeskLanes.Core.Services;

/// <summary>
/// 诊断包导出：把配置、日志与备份清单打包成一个 zip，供跨设备排障使用。
/// <para>
/// 跨设备场景里，用户一句"我这边打不开"几乎没有信息量。诊断包是唯一可靠的现场还原手段：
/// 配置（分类 / 分区 / 项目关联）、日志（同步与异常）、以及备份清单（判断是否发生过配置重置）。
/// </para>
/// 只用框架自带的 <see cref="ZipFile"/>，不引入任何第三方依赖。
/// 日志文件以 <see cref="FileShare.ReadWrite"/> 打开，导出时应用仍可继续写日志，不会互相锁死。
/// </summary>
public sealed class DiagnosticExporter
{
    /// <summary>单个文件超过这个体积就只记名字不打包内容（避免把诊断包撑成几十 MB）。</summary>
    private const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>要打包进诊断包的配置文件（存在才打包）。</summary>
    private static readonly string[] ConfigFiles = { "settings.json", "library.json" };

    private readonly string _configDir;

    /// <param name="configDir">配置目录（通常为 %LocalAppData%\DeskLanes）。</param>
    public DiagnosticExporter(string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir))
        {
            throw new ArgumentException("配置目录不能为空。", nameof(configDir));
        }

        _configDir = configDir;
    }

    /// <summary>日志目录（配置目录下的 logs）。</summary>
    public string LogsDirectory => Path.Combine(_configDir, "logs");

    /// <summary>备份目录（配置损坏 / 迁移时原文件的归档位置）。</summary>
    public string BackupsDirectory => Path.Combine(_configDir, "backups");

    /// <summary>
    /// 导出诊断包到指定 zip 路径。成功时 <see cref="OperationResult.ErrorMessage"/> 为 zip 的完整路径。
    /// </summary>
    public OperationResult Export(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            return OperationResult.Fail("没有指定诊断包的输出路径。");
        }

        try
        {
            string? dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            var skipped = new List<string>();

            using (FileStream zipStream = new(zipPath, FileMode.CreateNew, FileAccess.Write))
            using (ZipArchive zip = new(zipStream, ZipArchiveMode.Create))
            {
                foreach (string name in ConfigFiles)
                {
                    AddFile(zip, Path.Combine(_configDir, name), name, skipped);
                }

                if (Directory.Exists(LogsDirectory))
                {
                    // "*.log*" 同时命中当前份与轮转归档（sync.log.1 / errors.log.2）。
                    // 归档里往往是出事之前的那一段，排障时很有用，必须一起打包。
                    foreach (string full in Directory.EnumerateFiles(LogsDirectory, "*.log*"))
                    {
                        // zip 条目名必须用正斜杠：Path.Combine 在 Windows 上给的是反斜杠，
                        // 那样打包出来的条目名不会被标准解压工具识别为目录（冒烟测试第 16 节抓到过）
                        AddFile(zip, full, "logs/" + Path.GetFileName(full), skipped);
                    }
                }

                ZipArchiveEntry manifest = zip.CreateEntry("manifest.txt");
                using Stream target = manifest.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(BuildManifest(skipped));
                target.Write(bytes, 0, bytes.Length);
            }

            return new OperationResult(true, zipPath);
        }
        catch (IOException ex)
        {
            return OperationResult.Fail("写诊断包失败：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return OperationResult.Fail("没有写入该位置的权限：" + ex.Message);
        }
    }

    /// <summary>把单个文件加入压缩包；不存在则跳过，过大则只记名字。</summary>
    private static void AddFile(
        ZipArchive zip, string fullPath, string entryName,
        System.Collections.Generic.List<string> skipped)
    {
        if (!File.Exists(fullPath))
        {
            skipped.Add(entryName + "（不存在）");
            return;
        }

        long size = new FileInfo(fullPath).Length;
        if (size > MaxFileBytes)
        {
            skipped.Add($"{entryName}（{size / 1024} KB，超过 8 MB 未打包内容）");
            return;
        }

        try
        {
            // 关键：日志文件可能被应用同时写入，必须用 ReadWrite 共享打开
            using FileStream source = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
        catch (IOException)
        {
            skipped.Add(entryName + "（读取时正被占用）");
        }
    }

    /// <summary>生成包内说明：时间、运行环境、目录位置与跳过项。</summary>
    private string BuildManifest(System.Collections.Generic.List<string> skipped)
    {
        var sb = new StringBuilder();
        sb.Append("DeskLanes 诊断包\n");
        sb.Append("===============\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"导出时间（本地）: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append(CultureInfo.InvariantCulture, $"配置目录        : {_configDir}\n");
        sb.Append(CultureInfo.InvariantCulture, $"日志目录        : {LogsDirectory}\n");
        sb.Append(CultureInfo.InvariantCulture, $"操作系统        : {Environment.OSVersion}\n");
        sb.Append(CultureInfo.InvariantCulture, $".NET 运行时版本 : {Environment.Version}\n\n");

        sb.Append("内容：settings.json（设置）、library.json（项目/分类/分区）、logs/*.log（同步与异常日志）。\n\n");

        if (Directory.Exists(BackupsDirectory))
        {
            string[] backups = Directory.GetFiles(BackupsDirectory, "*.json");
            sb.Append(CultureInfo.InvariantCulture, $"配置备份        : {backups.Length} 个（只统计数量，未打包内容）\n");
            foreach (string b in backups)
            {
                sb.Append("  - ").Append(Path.GetFileName(b)).Append('\n');
            }

            sb.Append('\n');
        }

        if (skipped.Count > 0)
        {
            sb.Append("未打包的项：\n");
            foreach (string s in skipped)
            {
                sb.Append("  - ").Append(s).Append('\n');
            }

            sb.Append('\n');
        }

        sb.Append("注意：包内包含你的个人文件路径（桌面项目路径等），分享前请自行确认。\n");
        return sb.ToString();
    }
}
