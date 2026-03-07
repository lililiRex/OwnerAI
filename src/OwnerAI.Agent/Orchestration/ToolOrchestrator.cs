using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OwnerAI.Agent.Hooks;
using OwnerAI.Shared;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Orchestration;

/// <summary>
/// 工具调用编排器 — 管理工具注册、发现、执行，并桥接到 Microsoft.Extensions.AI function calling
/// </summary>
public sealed class ToolOrchestrator(
    IEnumerable<IOwnerAITool> tools,
    ISkillStateManager skillState,
    HookManager hookManager,
    ILogger<ToolOrchestrator> logger)
{
    private readonly Dictionary<string, (IOwnerAITool Tool, ToolAttribute Metadata)> _tools = BuildToolMap(tools);

    /// <summary>获取可用工具描述列表 (供 SystemPrompt 注入)</summary>
    public IReadOnlyList<string> GetAvailableTools(ToolContext context)
    {
        return _tools
            .Where(kv => IsToolEnabledForContext(kv.Key, kv.Value.Tool, context))
            .Select(kv => $"{kv.Value.Metadata.Name}: {kv.Value.Metadata.Description}")
            .ToList();
    }

    /// <summary>获取已启用的工具名集合（供 SystemPromptBuilder 条件输出）</summary>
    public IReadOnlySet<string> GetEnabledToolNames(ToolContext context)
    {
        return _tools
            .Where(kv => IsToolEnabledForContext(kv.Key, kv.Value.Tool, context))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>获取工具名列表</summary>
    public IReadOnlyList<string> GetToolNames()
        => [.. _tools.Keys];

    /// <summary>
    /// 将 IOwnerAITool 转换为 Microsoft.Extensions.AI 的 AITool 列表，
    /// 用于 ChatOptions.Tools 实现真正的 function calling
    /// </summary>
    public IList<AITool> GetAITools(ToolContext context)
    {
        var aiTools = new List<AITool>();

        foreach (var (name, (tool, meta)) in _tools)
        {
            if (!IsToolEnabledForContext(name, tool, context))
                continue;

            var paramSchema = BuildParameterJsonSchema(meta);

            // 使用自定义 ToolAIFunction — 显式 schema，无反射
            var capturedName = name;
            var func = new ToolAIFunction(
                name: capturedName,
                description: meta.Description,
                parameterSchema: paramSchema,
                handler: async (args, ct) =>
                {
                    var result = await ExecuteToolAsync(capturedName, args, context, ct);
                    return FormatToolResult(result);
                });

            aiTools.Add(func);
        }

        return aiTools;
    }

    /// <summary>执行工具</summary>
    public async Task<ToolResult> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (context.DisabledTools.Contains(toolName))
        {
            logger.LogInformation("[ToolOrchestrator] Tool {Tool} is disabled for session {Session}", toolName, context.SessionId);
            return ToolResult.Error($"工具 '{toolName}' 在当前任务中已禁用",
                errorCode: "tool_disabled_for_context",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "改用当前任务允许的工具，或调整任务策略后重试。");
        }

        if (!_tools.TryGetValue(toolName, out var entry))
        {
            logger.LogWarning("[ToolOrchestrator] Tool not found: {Tool}", toolName);
            return ToolResult.Error($"工具 '{toolName}' 不存在",
                errorCode: "tool_not_found",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "检查工具名称是否正确，以及该技能是否已启用。");
        }

        // Hook: BeforeToolCall — 允许钩子取消工具执行
        var beforeCtx = new HookContext
        {
            EventName = HookEvents.BeforeToolCall,
            Properties = new Dictionary<string, object>
            {
                ["toolName"] = toolName,
                ["parameters"] = parameters.ToString(),
                ["sessionId"] = context.SessionId,
            },
        };
        var beforeResult = await hookManager.DispatchAsync(HookEvents.BeforeToolCall, beforeCtx, ct);
        if (!beforeResult.Continue)
        {
            logger.LogInformation("[ToolOrchestrator] Tool {Tool} cancelled by BeforeToolCall hook", toolName);
            return ToolResult.Error($"工具 '{toolName}' 被钩子取消",
                errorCode: "hook_cancelled",
                failureCategory: ToolFailureCategory.PermissionDenied);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            logger.LogInformation("[ToolOrchestrator] Executing tool {Tool}", toolName);
            var result = await entry.Tool.ExecuteAsync(parameters, context, ct);
            sw.Stop();
            logger.LogInformation("[ToolOrchestrator] Tool {Tool} completed in {Elapsed}ms, success={Success}",
                toolName, sw.ElapsedMilliseconds, result.Success);

            // Hook: AfterToolCall — 通知钩子工具执行完成
            var afterCtx = new HookContext
            {
                EventName = HookEvents.AfterToolCall,
                Properties = new Dictionary<string, object>
                {
                    ["toolName"] = toolName,
                    ["success"] = result.Success,
                    ["elapsed"] = sw.ElapsedMilliseconds,
                    ["sessionId"] = context.SessionId,
                },
            };
            await hookManager.DispatchAsync(HookEvents.AfterToolCall, afterCtx, ct);

            return result with { Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[ToolOrchestrator] Tool {Tool} failed after {Elapsed}ms",
                toolName, sw.ElapsedMilliseconds);
            return ToolResult.Error($"工具执行失败: {ex.Message}",
                errorCode: "tool_exception",
                retryable: true,
                failureCategory: ToolFailureCategory.RetryableError,
                suggestedFix: "检查输入参数、外部依赖和运行环境后重试。") with
            {
                Duration = sw.Elapsed,
            };
        }
    }

    private static string FormatToolResult(ToolResult result)
    {
        if (result.Success)
            return result.Output ?? "(无输出)";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.ErrorCode))
            parts.Add($"错误码: {result.ErrorCode}");

        if (result.FailureCategory != ToolFailureCategory.Unknown)
            parts.Add($"错误分类: {result.FailureCategory}");

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            parts.Add($"错误: {result.ErrorMessage}");

        if (!string.IsNullOrWhiteSpace(result.SuggestedFix))
            parts.Add($"建议: {result.SuggestedFix}");

        parts.Add(result.Retryable ? "可重试: 是" : "可重试: 否");
        return string.Join(" | ", parts);
    }

    private bool IsToolEnabledForContext(string toolName, IOwnerAITool tool, ToolContext context)
        => !context.DisabledTools.Contains(toolName)
            && tool.IsAvailable(context)
            && skillState.IsEnabled(toolName);

    private static Dictionary<string, (IOwnerAITool, ToolAttribute)> BuildToolMap(IEnumerable<IOwnerAITool> tools)
    {
        var map = new Dictionary<string, (IOwnerAITool, ToolAttribute)>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var attr = tool.GetType().GetCustomAttributes(typeof(ToolAttribute), false)
                .OfType<ToolAttribute>()
                .FirstOrDefault();

            if (attr is not null)
            {
                map[attr.Name] = (tool, attr);
            }
        }
        return map;
    }

    private static JsonElement BuildParameterJsonSchema(ToolAttribute meta)
    {
        var schema = meta.Name switch
        {
            "run_command" => """{"type":"object","properties":{"command":{"type":"string","description":"要执行的 PowerShell 命令"},"working_directory":{"type":"string","description":"工作目录路径"}},"required":["command"]}""",
            "write_file" => """{"type":"object","properties":{"path":{"type":"string","description":"文件路径"},"content":{"type":"string","description":"文件内容"}},"required":["path","content"]}""",
            "read_file" => """{"type":"object","properties":{"path":{"type":"string","description":"文件路径"}},"required":["path"]}""",
            "list_directory" => """{"type":"object","properties":{"path":{"type":"string","description":"目录路径"}},"required":["path"]}""",
            "search_files" => """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"},"directory":{"type":"string","description":"搜索目录"}},"required":["query"]}""",
            "web_fetch" => """{"type":"object","properties":{"url":{"type":"string","description":"网页 URL"}},"required":["url"]}""",
            "web_search" => """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"}},"required":["query"]}""",
            "clipboard" => """{"type":"object","properties":{"action":{"type":"string","description":"操作类型: read 或 write"},"content":{"type":"string","description":"写入内容"}},"required":[]}""",
            "open_app" => """{"type":"object","properties":{"target":{"type":"string","description":"要打开的程序/文件/网址"},"arguments":{"type":"string","description":"命令行参数"}},"required":["target"]}""",
            "download_file" => """{"type":"object","properties":{"url":{"type":"string","description":"要下载的文件 URL（图片、文档等）"},"save_path":{"type":"string","description":"保存路径（可选，默认 Downloads 文件夹）"}},"required":["url"]}""",
            "download_video" => """{"type":"object","properties":{"url":{"type":"string","description":"视频页面 URL（YouTube、Bilibili 等）"},"save_directory":{"type":"string","description":"保存目录（可选）"},"quality":{"type":"string","description":"画质: best/1080p/720p/480p/audio（可选，默认 best）"}},"required":["url"]}""",
            "delegate_to_model" => """{"type":"object","properties":{"category":{"type":"string","description":"目标模型类别: llm/vision/multimodal/science/coding"},"task":{"type":"string","description":"要交给专业模型执行的任务描述"},"system_instruction":{"type":"string","description":"给专业模型的系统指令（可选）"}},"required":["category","task"]}""",
            "self_evolve" => """{"type":"object","properties":{"action":{"type":"string","description":"操作类型: report_gap/list_gaps/get_status/resolve_gap/plan_gap/list_plan/execute_step/complete_step/create_skill/deploy_build"},"description":{"type":"string","description":"能力缺口描述 (report_gap 时必填)"},"source":{"type":"string","description":"发现来源: user_feedback/tool_failure/self_analysis"},"priority":{"type":"integer","description":"优先级 1-5, 默认 3"},"status":{"type":"string","description":"状态过滤 (list_gaps 时可选)"},"gap_id":{"type":"string","description":"缺口 ID (resolve_gap/plan_gap/list_plan/execute_step 时必填)"},"resolution":{"type":"string","description":"解决方案描述 (resolve_gap 时必填)"},"steps":{"type":"array","description":"plan_gap 的实现计划数组。每项包含 title, description?, hypothesis?, acceptance_criteria?, step_type?, children?"},"plan":{"type":"array","description":"plan_gap 的兼容参数名，等同于 steps"},"step_id":{"type":"string","description":"complete_step 时必填"},"success":{"type":"boolean","description":"complete_step 是否成功，默认 true"},"result":{"type":"string","description":"步骤执行结果摘要"}},"required":["action"]}""",
            "schedule_task" => "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"create/list/cancel/pause/resume/status/history\"},\"name\":{\"type\":\"string\",\"description\":\"task name (create)\"},\"message\":{\"type\":\"string\",\"description\":\"prompt for agent (create)\"},\"type\":{\"type\":\"string\",\"description\":\"once/recurring/cron\"},\"delay_minutes\":{\"type\":\"integer\",\"description\":\"delay minutes\"},\"interval_minutes\":{\"type\":\"integer\",\"description\":\"recurring interval\"},\"cron_expression\":{\"type\":\"string\",\"description\":\"cron expr: min hour day month weekday\"},\"priority\":{\"type\":\"integer\",\"description\":\"1-5\"},\"description\":{\"type\":\"string\",\"description\":\"task description\"},\"task_id\":{\"type\":\"string\",\"description\":\"task id\"},\"status\":{\"type\":\"string\",\"description\":\"status filter\"},\"source\":{\"type\":\"string\",\"description\":\"source filter\"},\"limit\":{\"type\":\"integer\",\"description\":\"history limit\"}},\"required\":[\"action\"]}",
            "openclaw_skill" => """{"type":"object","properties":{"action":{"type":"string","description":"操作: list_skills(列出已安装技能), read_skill(读取技能指南), log_learning(记录学习日志), run_script(执行技能脚本)"},"skill_name":{"type":"string","description":"技能名称 (read_skill/run_script 时必填)"},"script":{"type":"string","description":"脚本相对路径如 scripts/activator.sh (run_script 时必填)"},"arguments":{"type":"string","description":"脚本额外参数 (run_script 时可选)"},"type":{"type":"string","description":"日志类型: learning/error/feature_request (log_learning 时使用)"},"content":{"type":"string","description":"日志内容 (log_learning 时必填)"}},"required":["action"]}""",
            "tcp_communicate" => """{"type":"object","properties":{"action":{"type":"string","description":"操作: connect/send/receive/disconnect/list_connections"},"host":{"type":"string","description":"目标主机地址 (connect 时必填)"},"port":{"type":"integer","description":"目标端口 (connect 时必填)"},"connection_id":{"type":"string","description":"连接标识 host:port (send/receive/disconnect 时必填)"},"data":{"type":"string","description":"要发送的数据 (send 时必填)"},"buffer_size":{"type":"integer","description":"接收缓冲区大小，默认 4096"},"timeout_ms":{"type":"integer","description":"接收超时毫秒数，默认 5000"},"encoding":{"type":"string","description":"编码: utf-8/ascii/gbk，默认 utf-8"}},"required":["action"]}""",
            "udp_communicate" => """{"type":"object","properties":{"action":{"type":"string","description":"操作: send/receive/broadcast/bind/close"},"host":{"type":"string","description":"目标主机地址 (send 时必填)"},"port":{"type":"integer","description":"端口 (send/broadcast/bind 时必填)"},"data":{"type":"string","description":"要发送的数据 (send/broadcast 时必填)"},"timeout_ms":{"type":"integer","description":"接收超时毫秒数，默认 5000"},"encoding":{"type":"string","description":"编码: utf-8/ascii/gbk，默认 utf-8"}},"required":["action"]}""",
            "serial_communicate" => """{"type":"object","properties":{"action":{"type":"string","description":"操作: list_ports/open/send/receive/close"},"port_name":{"type":"string","description":"串口名称如 COM3 (open 时必填)"},"baud_rate":{"type":"integer","description":"波特率，默认 9600"},"data_bits":{"type":"integer","description":"数据位，默认 8"},"parity":{"type":"string","description":"校验: none/odd/even/mark/space，默认 none"},"stop_bits":{"type":"string","description":"停止位: 1/1.5/2，默认 1"},"data":{"type":"string","description":"要发送的数据 (send 时必填)"},"hex":{"type":"boolean","description":"是否十六进制模式"},"buffer_size":{"type":"integer","description":"接收缓冲区大小，默认 1024"},"timeout_ms":{"type":"integer","description":"接收超时毫秒数，默认 5000"},"encoding":{"type":"string","description":"编码: utf-8/ascii/gbk，默认 utf-8"}},"required":["action"]}""",
            _ => """{"type":"object","properties":{}}""",
        };

        return JsonDocument.Parse(schema).RootElement;
    }
}
