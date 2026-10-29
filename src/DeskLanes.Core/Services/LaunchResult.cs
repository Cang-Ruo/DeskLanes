using DeskLanes.Core.Models;

namespace DeskLanes.Core.Services;

/// <summary>启动结果。</summary>
public sealed record LaunchResult(bool Success, string? ErrorMessage = null)
{
    public static LaunchResult Ok() => new(true);

    public static LaunchResult Fail(string message) => new(false, message);
}
