namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 钩子结果
/// </summary>
public sealed record HookResult
{
    public bool Continue { get; init; } = true;
    public IDictionary<string, object>? ModifiedProperties { get; init; }

    public static HookResult Ok() => new();
    public static HookResult Cancel() => new() { Continue = false };
}

/// <summary>
/// 钩子上下文
/// </summary>
public sealed record HookContext
{
    public required string EventName { get; init; }
    public IDictionary<string, object> Properties { get; init; } = new Dictionary<string, object>();
    public IServiceProvider? Services { get; init; }
}

/// <summary>
/// 钩子接口
/// </summary>
public interface IHook
{
    string EventName { get; }
    int Priority { get; }
    ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken ct);
}

/// <summary>
/// 钩子事件名称常量
/// </summary>
public static class HookEvents
{
    public const string BeforeAgentStart = "agent:before-start";
    public const string AfterAgentReply = "agent:after-reply";
    public const string BeforeToolCall = "tool:before-call";
    public const string AfterToolCall = "tool:after-call";
    public const string MessageReceived = "message:received";
    public const string MessageSending = "message:sending";
    public const string SessionCreated = "session:created";
    public const string SessionEnded = "session:ended";
    public const string ConfigChanged = "config:changed";
    public const string PluginLoaded = "plugin:loaded";
    public const string MemoryConsolidated = "memory:consolidated";
    public const string MemoryIngested = "memory:ingested";
}
