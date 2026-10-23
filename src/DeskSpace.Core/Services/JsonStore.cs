using System.IO;
using System.Text.Json;
using DeskSpace.Core.Models;

namespace DeskSpace.Core.Services;

/// <summary>
/// 通用 JSON 文件存储。安全要求：
/// <list type="bullet">
/// <item>原子写入：先写 .tmp 再替换，避免半截文件；替换前保留 .bak 备份。</item>
/// <item>文件不存在/为空：使用默认值，不报错。</item>
/// <item>JSON 损坏：原文件移入 backups/ 目录，重置为默认值并记录警告。</item>
/// <item>版本过新（由更高版本软件写入）：备份后重置为默认值并记录警告。</item>
/// <item>版本过旧：备份后按当前版本加载（缺失字段取默认值）。</item>
/// </list>
/// </summary>
public class JsonStore<T> where T : class, IVersionedData, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _filePath;
    private readonly string _backupDir;
    private readonly string _fileLabel;
    private readonly List<string> _warnings = new();

    public JsonStore(string baseDir, string fileName, string fileLabel)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            throw new ArgumentException("baseDir 不能为空", nameof(baseDir));
        }

        _filePath = Path.Combine(baseDir, fileName);
        _backupDir = Path.Combine(baseDir, "backups");
        _fileLabel = fileLabel;
    }

    /// <summary>数据文件完整路径。</summary>
    public string FilePath => _filePath;

    /// <summary>加载过程中的警告（配置损坏、已迁移、版本过新等）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>当前内存中的数据。从未加载过时为默认值。</summary>
    public T Data { get; private set; } = new();

    /// <summary>读取并解析数据文件。任何异常情况都不会抛出，而是降级为默认值 + 警告。</summary>
    public void Load()
    {
        _warnings.Clear();

        if (!File.Exists(_filePath))
        {
            Data = new T();
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (IOException ex)
        {
            _warnings.Add($"无法读取{_fileLabel}配置文件（{ex.Message}），已使用默认配置。");
            Data = new T();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // 空文件按首次使用处理
            Data = new T();
            return;
        }

        T? data;
        try
        {
            data = JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException ex)
        {
            BackupFile("corrupt");
            _warnings.Add($"{_fileLabel}配置文件已损坏（{ex.Message}），原文件已备份到 backups 目录，已恢复默认配置。");
            Data = new T();
            return;
        }

        if (data is null)
        {
            Data = new T();
            return;
        }

        int version = data.Version;
        if (version > DataVersions.Current)
        {
            BackupFile($"newer-v{version}");
            _warnings.Add($"{_fileLabel}配置文件版本（v{version}）比当前软件支持的最高版本（v{DataVersions.Current}）更新，原文件已备份，已恢复默认配置。");
            Data = new T();
            return;
        }

        if (version < DataVersions.Current)
        {
            BackupFile($"legacy-v{version}");
            _warnings.Add($"检测到旧版{_fileLabel}配置（v{version}），原文件已备份，已按当前版本加载（缺失字段使用默认值）。");

            // 迁移：内存中升级为当前版本，下次保存即写入新版本号
            data.Version = DataVersions.Current;
        }

        Data = data;
    }

    /// <summary>
    /// 保存数据。原子写入：.tmp → File.Replace（带 .bak 备份）或 File.Move。
    /// 失败时抛出 IOException，由调用方决定如何提示用户。
    /// </summary>
    public void Save()
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = _filePath + ".tmp";
        string json = JsonSerializer.Serialize(Data, Options);
        File.WriteAllText(tmpPath, json);

        if (File.Exists(_filePath))
        {
            string bakPath = _filePath + ".bak";
            if (File.Exists(bakPath))
            {
                File.Delete(bakPath);
            }

            File.Replace(tmpPath, _filePath, bakPath);
        }
        else
        {
            File.Move(tmpPath, _filePath);
        }
    }

    private void BackupFile(string tag)
    {
        try
        {
            Directory.CreateDirectory(_backupDir);
            string stem = Path.GetFileNameWithoutExtension(_filePath);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string target = Path.Combine(_backupDir, $"{stem}-{tag}-{stamp}.json");
            File.Copy(_filePath, target, overwrite: false);
        }
        catch (IOException)
        {
            // 备份失败不阻塞主流程（原文件仍在原位，用户可手动处理）
        }
    }
}
