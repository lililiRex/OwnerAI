namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 审批级别
/// </summary>
public enum ApprovalLevel
{
    /// <summary>通知用户但自动通过</summary>
    Low,
    /// <summary>弹窗确认</summary>
    Medium,
    /// <summary>弹窗确认 + 显示详细信息</summary>
    High,
    /// <summary>弹窗确认 + 详细信息 + 二次确认</summary>
    Critical,
}

/// <summary>
/// 审批结果
/// </summary>
public sealed record ApprovalResult
{
    public bool Approved { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public static ApprovalResult Allow(string? reason = null)
        => new() { Approved = true, Reason = reason };

    public static ApprovalResult Deny(string? reason = null)
        => new() { Approved = false, Reason = reason };
}

/// <summary>
/// 审批请求
/// </summary>
public sealed record ApprovalRequest
{
    public required string Operation { get; init; }
    public string? Details { get; init; }
    public required ApprovalLevel Level { get; init; }
    public required string SessionId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// 待审批条目
/// </summary>
public sealed record PendingApproval
{
    public required string Id { get; init; }
    public required ApprovalRequest Request { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 审批服务接口
/// </summary>
public interface IApprovalService
{
    Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken ct);
    IReadOnlyList<PendingApproval> GetPending();
}
