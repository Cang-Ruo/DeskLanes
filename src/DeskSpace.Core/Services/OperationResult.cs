namespace DeskSpace.Core.Services;

/// <summary>服务操作结果。失败时携带中文错误说明，由界面提示用户。</summary>
public sealed record OperationResult(bool Success, string? ErrorMessage)
{
    public static OperationResult Ok() => new(true, null);

    public static OperationResult Fail(string message) => new(false, message);
}
