using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>工作区库数据存储（%LocalAppData%\DeskLanes\library.json）。</summary>
public sealed class LibraryStore : JsonStore<LibraryData>
{
    public LibraryStore(string baseDir)
        : base(baseDir, "library.json", "工作区库")
    {
    }
}
