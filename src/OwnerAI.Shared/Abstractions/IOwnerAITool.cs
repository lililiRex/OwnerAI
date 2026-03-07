using System.Text.Json;

namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 安全级别
/// </summary>
public enum ToolSecurityLevel
{
    /// <summary>只读操作，无需审批</summary>
    ReadOnly = 0,
    /// <summary>低风险写操作，根据配置决定是否审批</summary>
    Low = 1,
    /// <summary>中风险操作（网络请求、文件修改），默认需要审批</summary>
    Medium = 2,
    /// <summary>高风险操作（删除文件、执行脚本、系统修改），始终需要审批</summary>
    High = 3,
    /// <summary>危险操作（管理员权限），需要二次确认</summary>
    Critical = 4,
}

/// <summary>
/// 工具元数据特性
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public ToolSecurityLevel SecurityLevel { get; init; } = ToolSecurityLevel.ReadOnly;
    public string[]? RequiredPermissions { get; init; }
    public bool RequiresSandbox { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// 工具参数描述特性
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class ToolParameterAttribute(string description) : Attribute
{
    public string Description { get; } = description;
    public bool Required { get; init; } = true;
}

/// <summary>
/// 工具失败分类 — 用于调度器和 Agent 做结构化判定
/// </summary>
public enum ToolFailureCategory
{
    Unknown = 0,
    ValidationError = 1,
    RetryableError = 2,
    EnvironmentError = 3,
    FatalError = 4,
    PermissionDenied = 5,
}

/// <summary>
/// 工具返回结果
/// </summary>
public sealed record ToolResult
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public ToolFailureCategory FailureCategory { get; init; }
    public bool Retryable { get; init; }
    public string? SuggestedFix { get; init; }
    public TimeSpan Duration { get; init; }
    public IDictionary<string, object>? Metadata { get; init; }

    /// <summary>工具提取的媒体资源 URL（图片、视频）— 用于 UI 内联展示，不落盘</summary>
    public IReadOnlyList<ToolMediaUrl>? MediaUrls { get; init; }

    public static ToolResult Ok(string output, IDictionary<string, object>? metadata = null)
        => new() { Success = true, Output = output, Metadata = metadata };

    public static ToolResult Error(
        string error,
        string? errorCode = null,
        bool retryable = false,
        string? suggestedFix = null,
        ToolFailureCategory? failureCategory = null,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            Success = false,
            ErrorMessage = error,
            ErrorCode = errorCode,
            FailureCategory = failureCategory ?? InferFailureCategory(error, errorCode, retryable),
            Retryable = retryable,
            SuggestedFix = suggestedFix,
            Metadata = metadata,
        };

    private static ToolFailureCategory InferFailureCategory(string error, string? errorCode, bool retryable)
    {
        if (retryable)
            return ToolFailureCategory.RetryableError;

        var code = errorCode ?? string.Empty;
        if (code.Contains("validation", StringComparison.OrdinalIgnoreCase)
            || code.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || code.Contains("missing", StringComparison.OrdinalIgnoreCase)
            || error.Contains("缺少参数", StringComparison.Ordinal)
            || error.Contains("不能为空", StringComparison.Ordinal))
            return ToolFailureCategory.ValidationError;

        if (code.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || error.Contains("权限", StringComparison.Ordinal)
            || error.Contains("拒绝", StringComparison.Ordinal))
            return ToolFailureCategory.PermissionDenied;

        if (code.Contains("environment", StringComparison.OrdinalIgnoreCase)
            || code.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || code.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || error.Contains("未配置", StringComparison.Ordinal)
            || error.Contains("不可用", StringComparison.Ordinal)
            || error.Contains("超时", StringComparison.Ordinal))
            return ToolFailureCategory.EnvironmentError;

        return ToolFailureCategory.FatalError;
    }
}

/// <summary>
/// 媒体资源类型
/// </summary>
public enum ToolMediaKind
{
    Image,
    Video,
}

/// <summary>
/// 工具提取的媒体资源 URL — 仅用于临时展示，不保存到本地
/// </summary>
public sealed record ToolMediaUrl(string Url, ToolMediaKind Kind, string? Alt = null);

/// <summary>
/// 工具上下文
/// </summary>
public sealed record ToolContext
{
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public bool IsOwner { get; init; } = true;
    public IReadOnlyDictionary<string, string> UserPermissions { get; init; }
        = new Dictionary<string, string>();
    public required IServiceProvider Services { get; init; }

    /// <summary>当前对话的用户附件 — 图片、视频等，供 ModelRouterTool 转发给次级模型</summary>
    public IReadOnlyList<OwnerAI.Shared.MediaAttachment>? Attachments { get; init; }

    /// <summary>禁用的工具名集合 — 由上层任务策略注入</summary>
    public IReadOnlySet<string> DisabledTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 工具接口
/// </summary>
public interface IOwnerAITool
{
    /// <summary>工具是否在当前上下文中可用</summary>
    bool IsAvailable(ToolContext context);

    /// <summary>执行工具</summary>
    ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct = default);
}
