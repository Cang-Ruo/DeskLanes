namespace DeskLanes.Core.Models;

/// <summary>带版本号的数据契约，供 <see cref="Services.JsonStore{T}"/> 做版本检查与迁移。</summary>
public interface IVersionedData
{
    int Version { get; set; }
}
