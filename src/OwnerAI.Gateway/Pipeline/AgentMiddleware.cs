using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Agent;
using OwnerAI.Configuration;
using OwnerAI.Gateway.Sessions;
using OwnerAI.Shared;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// Agent 调用中间件 — 将路由后的消息交给 Agent 执行，收集回复
/// </summary>
public sealed class AgentMiddleware(ILogger<AgentMiddleware> logger) : IGatewayMiddleware
{
    private static readonly string[] s_fastModePrefixes = ["/fast ", "/quick ", "#fast ", "快答:", "快速:"];

    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct)
    {
        var agent = context.Services.GetRequiredService<IAgent>();
        var config = context.Services.GetRequiredService<IOptions<OwnerAIConfig>>().Value;
        var conversationHistory = context.Services.GetRequiredService<ConversationHistory>();

        // 加载会话对话历史 — 为模型提供多轮对话上下文
        var history = conversationHistory.GetMessages(context.SessionId);
        var (normalizedMessage, workCategory) = ResolveWorkCategory(context.Message.Text, context.Message.Attachments);

        var agentContext = new AgentContext
        {
            SessionId = context.SessionId,
            UserMessage = normalizedMessage,
            Config = config.Agent,
            History = history,
            Attachments = context.Message.Attachments,
            WorkCategory = workCategory,
        };

        logger.LogInformation("[AgentMiddleware] Invoking agent for session {Session}", context.SessionId);

        var responseText = new System.Text.StringBuilder();
        var toolCalls = new List<ToolCallInfo>();
        var modelInteractions = new List<ModelInteraction>();
        var mediaAttachments = new List<MediaAttachment>();

        await foreach (var chunk in agent.ExecuteAsync(agentContext, ct))
        {
            if (chunk.Text is { Length: > 0 })
            {
                responseText.Append(chunk.Text);

                // 实时推送到 UI
                context.OnStreamChunk?.Invoke(chunk.Text);
            }

            if (chunk.ToolCall is not null)
            {
                toolCalls.Add(chunk.ToolCall);
                context.OnToolCall?.Invoke(chunk.ToolCall);
            }

            if (chunk.ModelEvent is not null)
            {
                modelInteractions.Add(chunk.ModelEvent);
                context.OnModelEvent?.Invoke(chunk.ModelEvent);
            }

            // 收集工具提取的媒体资源（限制数量，避免大量图片淹没文字内容）
            if (chunk.MediaUrls is { Count: > 0 })
            {
                const int MaxImages = 4;
                const int MaxVideos = 2;

                foreach (var media in chunk.MediaUrls)
                {
                    // 按类型限制数量
                    var imageCount = mediaAttachments.Count(a => a.ContentType.StartsWith("image/", StringComparison.Ordinal));
                    var videoCount = mediaAttachments.Count(a => a.ContentType.StartsWith("video/", StringComparison.Ordinal));

                    if (media.Kind == ToolMediaKind.Image && imageCount >= MaxImages)
                        continue;
                    if (media.Kind == ToolMediaKind.Video && videoCount >= MaxVideos)
                        continue;

                    var contentType = media.Kind switch
                    {
                        ToolMediaKind.Image => "image/jpeg",
                        ToolMediaKind.Video => "video/mp4",
                        _ => "application/octet-stream",
                    };

                    mediaAttachments.Add(new MediaAttachment
                    {
                        FileName = media.Alt ?? (media.Kind == ToolMediaKind.Image ? "web_image" : "web_video"),
                        ContentType = contentType,
                        Url = media.Url,
                    });
                }
            }
        }

        // 将本轮对话存入会话历史 — 供后续轮次使用
        conversationHistory.Append(context.SessionId,
            new ChatMessage(ChatRole.User, context.Message.Text));

        var replyText = responseText.ToString();
        if (replyText.Length > 0)
        {
            conversationHistory.Append(context.SessionId,
                new ChatMessage(ChatRole.Assistant, replyText));
        }

        if (responseText.Length > 0 || toolCalls.Count > 0)
        {
            context.Response = new ReplyPayload
            {
                Text = replyText,
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                ModelInteractions = modelInteractions.Count > 0 ? modelInteractions : null,
                Attachments = mediaAttachments.Count > 0 ? mediaAttachments : null,
            };
        }

        await next(context, ct);
    }

    private static (string Message, ModelWorkCategory WorkCategory) ResolveWorkCategory(
        string message,
        IReadOnlyList<MediaAttachment>? attachments)
    {
        if (attachments is { Count: > 0 })
            return (message, ModelWorkCategory.VisionAssist);

        var normalized = message.Trim();
        foreach (var prefix in s_fastModePrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = normalized[prefix.Length..].Trim();
                return (string.IsNullOrWhiteSpace(stripped) ? normalized : stripped, ModelWorkCategory.ChatFast);
            }
        }

        return IsFastChatCandidate(normalized)
            ? (normalized, ModelWorkCategory.ChatFast)
            : (normalized, ModelWorkCategory.ChatDefault);
    }

    private static bool IsFastChatCandidate(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 40)
            return false;

        if (message.Contains('\n') || message.Contains("```", StringComparison.Ordinal))
            return false;

        string[] slowKeywords = ["代码", "实现", "重构", "编译", "报错", "异常", "项目", ".cs", ".xaml", "SchedulerService", "设计", "方案", "架构"];
        return !slowKeywords.Any(message.Contains);
    }
}
