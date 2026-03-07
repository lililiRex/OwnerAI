using Microsoft.Extensions.AI;
using OwnerAI.Configuration;
using OwnerAI.Shared;

namespace OwnerAI.Agent;

/// <summary>
/// Agent 角色
/// </summary>
public enum AgentRole
{
    Chat = 0,
    Evolution = 1,
}

/// <summary>
/// Agent 运行上下文
/// </summary>
public sealed record AgentContext
{
    /// <summary>会话 ID</summary>
    public required string SessionId { get; init; }

    /// <summary>用户消息</summary>
    public required string UserMessage { get; init; }

    /// <summary>对话历史</summary>
    public IReadOnlyList<ChatMessage> History { get; init; } = [];

    /// <summary>Agent 配置</summary>
    public required AgentConfig Config { get; init; }

    /// <summary>用户附件 — 图片、视频、文档等媒体文件</summary>
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }

    /// <summary>工作分类槽位 — 指定本次 Agent 调用优先使用哪类模型</summary>
    public ModelWorkCategory WorkCategory { get; init; } = ModelWorkCategory.ChatDefault;

    /// <summary>当前 Agent 角色 — 聊天/工作 或 进化</summary>
    public AgentRole Role { get; init; } = AgentRole.Chat;

    /// <summary>禁用的工具名集合 — 用于任务级收窄工具面</summary>
    public IReadOnlySet<string> DisabledTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>最终回复文本 (执行完成后设置)</summary>
    public string? Response { get; set; }
}
