using DeskSpace.Core.Models;

namespace DeskSpace.Core.Services;

/// <summary>应用设置存储（%LocalAppData%\DeskSpace\settings.json）。</summary>
public sealed class ConfigStore : JsonStore<AppSettings>
{
    public ConfigStore(string baseDir)
        : base(baseDir, "settings.json", "应用设置")
    {
    }
}
