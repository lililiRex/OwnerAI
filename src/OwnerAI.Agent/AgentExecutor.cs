using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Agent.Context;
using OwnerAI.Agent.Hooks;
using OwnerAI.Agent.Orchestration;
using OwnerAI.Agent.Providers;
using OwnerAI.Configuration;
using OwnerAI.Shared;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent;

/// <summary>
/// Agent 执行器 — 多轮工具调用的 ReAct 推理循环
/// 主模型驱动对话与任务规划，次级模型通过 delegate_to_model 按需调度
/// </summary>
public sealed class AgentExecutor(
    IChatClient chatClient,
    ToolOrchestrator toolOrchestrator,
    IMemoryManager memory,
    ITaskCacheManager taskCache,
    ProviderRegistry providerRegistry,
    HookManager hookManager,
    IEventBus eventBus,
    IOptions<OwnerAIConfig> ownerAiOptions,
    ILogger<AgentExecutor> logger) : IAgent
{
    private readonly IEventBus _eventBus = eventBus;
    private readonly ProviderFailover? _providerFailover = chatClient as ProviderFailover;
    private readonly OwnerAIConfig _ownerAiConfig = ownerAiOptions.Value;

    /// <summary>
    /// 序列化选项 — 显式启用反射解析器，避免 NativeAOT/trimming 环境下报错
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
    };

    public async IAsyncEnumerable<AgentStreamChunk> ExecuteAsync(
        AgentContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var effectiveContext = context with { Config = ResolveRoleConfig(context) };

        // Hook: BeforeAgentStart
        var startHookCtx = new HookContext
        {
            EventName = HookEvents.BeforeAgentStart,
            Properties = new Dictionary<string, object>
            {
                ["sessionId"] = effectiveContext.SessionId,
                ["userMessage"] = effectiveContext.UserMessage,
                ["role"] = effectiveContext.Role.ToString(),
            },
        };
        await hookManager.DispatchAsync(HookEvents.BeforeAgentStart, startHookCtx, ct);

        using var workCategoryScope = _providerFailover is not null
            ? ProviderFailover.BeginWorkCategoryScope(effectiveContext.WorkCategory)
            : null;

        // 1. 检索相关记忆
        var memories = await memory.SearchAsync(effectiveContext.UserMessage, topK: 5, ct: ct);
        var profile = await memory.GetUserProfileAsync("owner", ct);

        // 2. 搜索任务缓存 — 三模式判定
        var cacheResult = await taskCache.SearchAsync(effectiveContext.UserMessage, ct);
        var hitMode = cacheResult?.HitMode ?? TaskCacheHitMode.Miss;

        logger.LogInformation("[Agent-{Role}] TaskCache: mode={Mode}, score={Score:F3}",
            effectiveContext.Role, hitMode, cacheResult?.Score ?? 0f);

        // === 精确命中 — 跳过 ReAct 循环，直接返回缓存结果 ===
        if (hitMode == TaskCacheHitMode.ExactHit)
        {
            logger.LogInformation("[Agent] ExactHit — returning cached result for \"{Query}\"",
                cacheResult!.Entry.Query);

            var cachedText = cacheResult.Entry.Result;
            context.Response = cachedText;

            yield return new AgentStreamChunk(cachedText);
            yield return new AgentStreamChunk { IsComplete = true };

            // 摄入对话到记忆（即使命中缓存也记录对话）
            await memory.IngestConversationAsync(
                effectiveContext.SessionId, effectiveContext.UserMessage, cachedText, ct);

            logger.LogInformation("[Agent-{Role}] ExactHit execution complete for session {Session}", effectiveContext.Role, effectiveContext.SessionId);
            yield break;
        }

        // === 参考命中或未命中 — 继续正常 ReAct 流程 ===

        // 3. 构建工具上下文
        var toolContext = new ToolContext
        {
            SessionId = effectiveContext.SessionId,
            AgentId = GetAgentDisplayName(effectiveContext.Role),
            Services = null!,
            Attachments = effectiveContext.Attachments,
            DisabledTools = effectiveContext.DisabledTools,
        };

        // 4. 构建系统提示词（参考命中时注入历史思考链）
        var enabledToolNames = toolOrchestrator.GetEnabledToolNames(toolContext);
        var promptBuilder = new SystemPromptBuilder()
            .WithPersona(effectiveContext.Config.Persona)
            .WithUserProfile(profile)
            .WithRetrievedMemories(memories)
            .WithTools(toolOrchestrator.GetAvailableTools(toolContext))
            .WithEnabledTools(enabledToolNames)
            .WithModelTeam(providerRegistry.GetModelTeam())
            .WithDateTime(DateTimeOffset.Now);

        if (hitMode == TaskCacheHitMode.ReferenceHit)
        {
            promptBuilder.WithTaskReference(cacheResult);
            logger.LogInformation("[Agent] ReferenceHit — injecting thinking chain as few-shot");
        }

        var systemPrompt = promptBuilder.Build();

        // 5. 构建消息列表
        var ctxManager = new ContextWindowManager(effectiveContext.Config.ContextWindowTokenBudget);
        var trimmedHistory = ctxManager.TrimHistory(effectiveContext.History, systemPrompt);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt)
        };
        messages.AddRange(trimmedHistory);
        messages.Add(BuildUserMessage(effectiveContext));

        // 6. 获取 AI 工具列表（function calling schema）
        // 如果主模型不支持工具调用（如 DeepSeek R1），则跳过工具注入
        var supportsTools = providerRegistry.PrimarySupportsTools();
        var aiTools = supportsTools
            ? toolOrchestrator.GetAITools(toolContext)
            : [];

        var options = new ChatOptions
        {
            Temperature = effectiveContext.Config.Temperature,
            Tools = aiTools,
        };

        if (!supportsTools)
        {
            logger.LogInformation("[Agent] Primary model does not support tools — running in pure conversation mode");
        }

        // 7. ReAct 推理循环 — 同时收集思考/工作步骤用于缓存
        var fullResponse = new StringBuilder();
        var allToolCalls = new List<ToolCallInfo>();
        var thinkingSteps = new List<ThinkingStep>();
        var workSteps = new List<WorkStep>();

        var maxRounds = effectiveContext.Config.MaxToolIterations;

        for (var round = 0; round < maxRounds; round++)
        {
            // 调用 LLM（流式输出，实时推送思考过程到 UI）
            var textParts = new StringBuilder();
            var functionCalls = new List<FunctionCallContent>();
            var streamUpdates = new List<ChatResponseUpdate>();

            // 手动管理枚举器 — 首次迭代时检测工具兼容性错误（yield 不可在 try-catch 中使用）
            var enumerator = chatClient.GetStreamingResponseAsync(messages, options, ct)
                .GetAsyncEnumerator(ct);
            bool hasFirst;

            try
            {
                hasFirst = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (
                ex.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase) ||
                ex.InnerException?.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase) == true)
            {
                // 模型不支持工具调用 — 清除工具列表并以流式重试
                logger.LogWarning("[Agent] Model does not support tools — retrying without tools");
                await enumerator.DisposeAsync();
                options.Tools = [];
                enumerator = chatClient.GetStreamingResponseAsync(messages, options, ct)
                    .GetAsyncEnumerator(ct);
                hasFirst = await enumerator.MoveNextAsync();
            }

            // 流式读取并实时推送文本到 UI — 用户立即看到思考过程
            try
            {
                if (hasFirst)
                {
                    do
                    {
                        var update = enumerator.Current;
                        streamUpdates.Add(update);

                        if (update.Text is { Length: > 0 })
                        {
                            textParts.Append(update.Text);
                            fullResponse.Append(update.Text);
                            yield return new AgentStreamChunk(update.Text);
                        }

                        foreach (var content in update.Contents)
                        {
                            if (content is FunctionCallContent fc)
                                functionCalls.Add(fc);
                        }
                    } while (await enumerator.MoveNextAsync());
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            // 记录思考过程（表2）
            if (textParts.Length > 0)
            {
                thinkingSteps.Add(new ThinkingStep
                {
                    Round = round,
                    Reasoning = textParts.ToString(),
                });
            }

            // 将 assistant 消息加入历史（从流式更新重建）
            messages.AddMessages(streamUpdates);

            // 如果没有工具调用，推理结束
            if (functionCalls.Count == 0)
                break;

            // 执行所有工具调用
            var toolResultContents = new List<AIContent>();
            foreach (var fc in functionCalls)
            {
                var argsString = SerializeArgsSafe(fc.Arguments);

                logger.LogInformation("[Agent] Tool call: {Tool}({Args})", fc.Name, argsString);

                // 检测模型分发 — 在执行前发送分发请求事件
                ModelInteraction? delegationRequest = null;
                if (fc.Name == "delegate_to_model")
                {
                    delegationRequest = BuildDelegationRequest(fc.Arguments);
                    if (delegationRequest is not null)
                    {
                        yield return new AgentStreamChunk { ModelEvent = delegationRequest };
                    }
                }

                // 执行工具 — 将 Arguments 转为 JsonElement
                var argsJson = fc.Arguments is not null
                    ? JsonSerializer.SerializeToElement(fc.Arguments, s_jsonOptions)
                    : JsonSerializer.SerializeToElement(new { }, s_jsonOptions);

                var toolResult = await toolOrchestrator.ExecuteToolAsync(
                    fc.Name, argsJson, toolContext, ct);

                var resultText = FormatToolResult(toolResult);

                logger.LogInformation("[Agent] Tool {Tool} result: success={Success}, length={Length}",
                    fc.Name, toolResult.Success, resultText.Length);

                yield return new AgentStreamChunk
                {
                    ToolCall = new ToolCallInfo
                    {
                        ToolName = fc.Name,
                        Parameters = argsString,
                        Result = resultText.Length > 1000
                            ? string.Concat(resultText.AsSpan(0, 1000), "...")
                            : resultText,
                        ErrorMessage = toolResult.ErrorMessage,
                        ErrorCode = toolResult.ErrorCode,
                        FailureCategory = toolResult.FailureCategory,
                        Retryable = toolResult.Retryable,
                        SuggestedFix = toolResult.SuggestedFix,
                        Success = toolResult.Success,
                        Duration = toolResult.Duration,
                    }
                };

                // 检测模型分发 — 执行后发送次级模型回复事件
                if (delegationRequest is not null)
                {
                    yield return new AgentStreamChunk
                    {
                        ModelEvent = delegationRequest with
                        {
                            IsRequest = false,
                            Response = resultText,
                        }
                    };
                }

                // 推送工具提取的媒体资源 — 用于 UI 内联展示
                if (toolResult.MediaUrls is { Count: > 0 })
                {
                    yield return new AgentStreamChunk
                    {
                        MediaUrls = toolResult.MediaUrls,
                    };
                }

                // 构建 FunctionResultContent
                toolResultContents.Add(new FunctionResultContent(fc.CallId, resultText));

                var resultSummary = resultText.Length > 500
                    ? string.Concat(resultText.AsSpan(0, 500), "...")
                    : resultText;

                // 记录完整的工具调用信息
                allToolCalls.Add(new ToolCallInfo
                {
                    ToolName = fc.Name,
                    Parameters = argsString,
                    Result = resultSummary,
                    ErrorMessage = toolResult.ErrorMessage,
                    ErrorCode = toolResult.ErrorCode,
                    FailureCategory = toolResult.FailureCategory,
                    Retryable = toolResult.Retryable,
                    SuggestedFix = toolResult.SuggestedFix,
                    Success = toolResult.Success,
                    Duration = toolResult.Duration,
                });

                // 记录工作过程（表3）
                workSteps.Add(new WorkStep
                {
                    Round = round,
                    ToolName = fc.Name,
                    Parameters = argsString,
                    Result = resultSummary,
                    Success = toolResult.Success,
                });
            }

            // 将工具结果加入消息历史
            messages.Add(new ChatMessage(ChatRole.Tool, toolResultContents));

            if (round == maxRounds - 1)
            {
                logger.LogWarning("[Agent-{Role}] ⚠ 已达最大工具调用轮次 ({MaxRounds})，强制结束 ReAct 循环。session={Session}",
                    effectiveContext.Role, maxRounds, effectiveContext.SessionId);
            }
        }

        // 8. 设置最终回复
        context.Response = fullResponse.ToString();

        // 9. 摄入对话到记忆
        if (context.Response.Length > 0)
        {
            await memory.IngestConversationAsync(
                context.SessionId,
                context.UserMessage,
                context.Response,
                ct);
        }

        // 10. 存储任务缓存 — 仅在有实质回复时存储
        if (context.Response.Length > 0)
        {
            var cacheEntry = new TaskCacheEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Query = context.UserMessage,
                Result = context.Response,
                IsIdempotent = workSteps.Count == 0, // 无工具调用 = 幂等
                ThinkingSteps = thinkingSteps,
                WorkSteps = workSteps,
            };
            await taskCache.StoreAsync(cacheEntry, ct);
        }

        // Hook: AfterAgentReply
        var replyHookCtx = new HookContext
        {
            EventName = HookEvents.AfterAgentReply,
            Properties = new Dictionary<string, object>
            {
                ["sessionId"] = context.SessionId,
                ["response"] = context.Response,
                ["toolCallCount"] = allToolCalls.Count,
            },
        };
        await hookManager.DispatchAsync(HookEvents.AfterAgentReply, replyHookCtx, ct);

        yield return new AgentStreamChunk { IsComplete = true };
        logger.LogInformation("[Agent] Execution complete for session {Session}", context.SessionId);
    }

    /// <summary>
    /// 安全序列化函数调用参数 — 避免 NativeAOT 环境下反射序列化错误
    /// </summary>
    private static string SerializeArgsSafe(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "{}";

        try
        {
            return JsonSerializer.Serialize(arguments, s_jsonOptions);
        }
        catch
        {
            // 回退：手动拼接
            var sb = new StringBuilder("{");
            var first = true;
            foreach (var (key, value) in arguments)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(key).Append("\":\"").Append(value).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }
    }

    private static string FormatToolResult(ToolResult toolResult)
    {
        if (toolResult.Success)
            return toolResult.Output ?? "(无输出)";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(toolResult.ErrorCode))
            parts.Add($"错误码: {toolResult.ErrorCode}");

        if (!string.IsNullOrWhiteSpace(toolResult.ErrorMessage))
            parts.Add($"错误: {toolResult.ErrorMessage}");

        if (!string.IsNullOrWhiteSpace(toolResult.SuggestedFix))
            parts.Add($"建议: {toolResult.SuggestedFix}");

        parts.Add(toolResult.Retryable ? "可重试: 是" : "可重试: 否");
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// 从 delegate_to_model 的参数中构建分发请求事件
    /// </summary>
    private ModelInteraction? BuildDelegationRequest(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;

        var category = arguments.TryGetValue("category", out var catObj)
            ? catObj?.ToString() ?? "unknown"
            : "unknown";

        var task = arguments.TryGetValue("task", out var taskObj)
            ? taskObj?.ToString() ?? ""
            : "";

        // 从 ProviderRegistry 查找模型名称
        var modelName = category;
        if (TryParseCategory(category, out var modelCategory))
        {
            var entry = providerRegistry.GetByCategory(modelCategory);
            if (entry is not null)
                modelName = entry.Name;
        }

        return new ModelInteraction
        {
            IsRequest = true,
            ModelName = modelName,
            Category = category,
            Task = task,
        };
    }

    private static bool TryParseCategory(string? value, out Configuration.ModelCategory category)
    {
        category = value?.ToLowerInvariant() switch
        {
            "llm" or "language" or "text" => Configuration.ModelCategory.LLM,
            "vision" or "visual" or "image" => Configuration.ModelCategory.Vision,
            "multimodal" or "multi" => Configuration.ModelCategory.Multimodal,
            "science" or "math" or "reasoning" => Configuration.ModelCategory.Science,
            "coding" or "code" or "coder" => Configuration.ModelCategory.Coding,
            _ => default,
        };
        return value is not null && (category != default || value.Equals("llm", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 构建用户 ChatMessage — 如有图片/视频附件则构建多模态消息（含 DataContent）
    /// </summary>
    private ChatMessage BuildUserMessage(AgentContext context)
    {
        if (context.Attachments is not { Count: > 0 })
            return new ChatMessage(ChatRole.User, context.UserMessage);

        var contents = new List<AIContent> { new TextContent(context.UserMessage) };

        foreach (var att in context.Attachments)
        {
            try
            {
                var content = LoadAttachmentContent(att);
                if (content is not null)
                    contents.Add(content);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Agent] Failed to load attachment: {File}", att.FileName);
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    /// <summary>
    /// 将 MediaAttachment 转为 DataContent — 支持内联 Data、本地文件、远程 URL
    /// </summary>
    internal static DataContent? LoadAttachmentContent(MediaAttachment att)
    {
        // 仅处理图片和视频类型（文档等暂不作为多模态内容）
        if (!att.ContentType.StartsWith("image/", StringComparison.Ordinal) &&
            !att.ContentType.StartsWith("video/", StringComparison.Ordinal))
            return null;

        // 优先使用内联数据
        if (att.Data is { Length: > 0 })
            return new DataContent(att.Data, att.ContentType);

        // 本地文件 — 读取字节
        if (att.Url is not null && File.Exists(att.Url))
        {
            var bytes = File.ReadAllBytes(att.Url);
            return new DataContent(bytes, att.ContentType);
        }

        // 远程 URL
        if (att.Url is not null && Uri.TryCreate(att.Url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            return new DataContent(uri, att.ContentType);

        return null;
    }

    private AgentConfig ResolveRoleConfig(AgentContext context)
    {
        var roleConfig = context.Role switch
        {
            AgentRole.Evolution => _ownerAiConfig.EvolutionAgent,
            _ => _ownerAiConfig.ChatAgent,
        };

        return context.Config with
        {
            DefaultModel = roleConfig.DefaultModel ?? context.Config.DefaultModel,
            FallbackModel = roleConfig.FallbackModel ?? context.Config.FallbackModel,
            Temperature = roleConfig.Temperature ?? context.Config.Temperature,
            MaxToolIterations = roleConfig.MaxToolIterations ?? context.Config.MaxToolIterations,
            ContextWindowTokenBudget = roleConfig.ContextWindowTokenBudget ?? context.Config.ContextWindowTokenBudget,
            Persona = roleConfig.Persona ?? context.Config.Persona,
        };
    }

    private string GetAgentDisplayName(AgentRole role)
        => role switch
        {
            AgentRole.Evolution => _ownerAiConfig.EvolutionAgent.DisplayName,
            _ => _ownerAiConfig.ChatAgent.DisplayName,
        };
}
