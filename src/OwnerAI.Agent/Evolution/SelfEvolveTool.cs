using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Evolution;

/// <summary>
/// 自我进化工具 — AI 在对话中主动报告能力缺口、查看进化状态、树形规划与分步实现
/// <para>当 AI 发现自己无法完成某项任务、或识别到某个能力缺口时，可调用此工具记录。</para>
/// <para>后台进化线程会自动拾取并尝试实现。</para>
/// </summary>
[Tool("self_evolve", "自我进化工具 — 报告能力缺口、树形规划实现计划、分步执行、查看状态。当你发现无法完成某任务或识别到新能力需求时使用",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 120)]
public sealed class SelfEvolveTool(
    IEvolutionManager evolutionManager,
    ILogger<SelfEvolveTool> logger) : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("action", out var actionEl))
            return ToolResult.Error(
                "缺少参数: action (可选: report_gap, list_gaps, get_status, resolve_gap, plan_gap, list_plan, execute_step, complete_step, create_skill, deploy_build)",
                errorCode: "missing_action",
                suggestedFix: "为 self_evolve 提供 action 参数，例如 action=plan_gap。",
                retryable: true);

        var action = actionEl.GetString()?.ToLowerInvariant();

        return action switch
        {
            "report_gap" => await ReportGapAsync(parameters, ct),
            "list_gaps" => await ListGapsAsync(parameters, ct),
            "get_status" => await GetStatusAsync(ct),
            "resolve_gap" => await ResolveGapAsync(parameters, ct),
            "plan_gap" => await PlanGapAsync(parameters, ct),
            "list_plan" => await ListPlanAsync(parameters, ct),
            "execute_step" => await ExecuteStepAsync(parameters, ct),
            "complete_step" => await CompleteStepAsync(parameters, ct),
            "create_skill" => await CreateSkillAsync(parameters, ct),
            "deploy_build" => await DeployBuildAsync(parameters, ct),
            _ => ToolResult.Error(
                $"未知操作: {action}。可选: report_gap, list_gaps, get_status, resolve_gap, plan_gap, list_plan, execute_step, complete_step, create_skill, deploy_build",
                errorCode: "unknown_action",
                suggestedFix: "将 action 改为 self_evolve 支持的固定操作名。",
                retryable: true),
        };
    }

    private async Task<ToolResult> ReportGapAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("description", out var descEl))
            return ToolResult.Error("缺少参数: description (能力缺口描述)");

        var description = descEl.GetString();
        if (string.IsNullOrWhiteSpace(description))
            return ToolResult.Error("description 不能为空");

        var source = parameters.TryGetProperty("source", out var srcEl)
            ? srcEl.GetString() ?? "self_analysis"
            : "self_analysis";

        var priority = parameters.TryGetProperty("priority", out var priEl)
            && priEl.TryGetInt32(out var p) ? p : 3;

        var category = parameters.TryGetProperty("category", out var catEl)
            ? catEl.GetString() ?? "source"
            : "source";

        var id = await evolutionManager.ReportGapAsync(description, source, priority, category: category, ct: ct);

        if (string.IsNullOrEmpty(id))
            return ToolResult.Ok("已有相似的能力缺口在跟踪中，跳过重复报告。");

        var categoryLabel = category == "skill" ? "技能" : "源码";
        logger.LogInformation("[SelfEvolve] Gap reported: {Id} [{Category}] — {Desc}", id, category, description);
        return ToolResult.Ok($"✅ 能力缺口已记录 (ID: {id})，后台进化线程将自动尝试实现。\n缺口: {description}\n类别: {categoryLabel}\n优先级: {priority}/5\n来源: {source}");
    }

    private async Task<ToolResult> ListGapsAsync(JsonElement parameters, CancellationToken ct)
    {
        EvolutionGapStatus? statusFilter = null;
        if (parameters.TryGetProperty("status", out var statusEl))
        {
            var statusStr = statusEl.GetString()?.ToLowerInvariant();
            statusFilter = statusStr switch
            {
                "detected" => EvolutionGapStatus.Detected,
                "planning" => EvolutionGapStatus.Planning,
                "implementing" => EvolutionGapStatus.Implementing,
                "verifying" => EvolutionGapStatus.Verifying,
                "resolved" => EvolutionGapStatus.Resolved,
                "failed" => EvolutionGapStatus.Failed,
                "deferred" => EvolutionGapStatus.Deferred,
                _ => null,
            };
        }

        var gaps = await evolutionManager.ListGapsAsync(statusFilter, ct);

        if (gaps.Count == 0)
            return ToolResult.Ok(statusFilter.HasValue
                ? $"没有状态为 {statusFilter.Value} 的能力缺口。"
                : "当前没有记录的能力缺口。系统运行良好！");

        var sb = new StringBuilder();
        sb.AppendLine($"# 能力缺口列表 ({gaps.Count} 条)");
        sb.AppendLine();

        foreach (var gap in gaps)
        {
            var statusIcon = gap.Status switch
            {
                EvolutionGapStatus.Detected => "🔍",
                EvolutionGapStatus.Planning => "📋",
                EvolutionGapStatus.Implementing => "🔧",
                EvolutionGapStatus.Verifying => "🧪",
                EvolutionGapStatus.Resolved => "✅",
                EvolutionGapStatus.Failed => "❌",
                EvolutionGapStatus.Deferred => "⏸️",
                _ => "❓",
            };

            var catIcon = gap.Category == "skill" ? "🧩" : "💻";
            sb.AppendLine($"{statusIcon} {catIcon} **{gap.Id}** [P{gap.Priority}] {gap.Description}");
            sb.AppendLine($"   状态: {gap.Status} | 类别: {gap.Category} | 来源: {gap.Source} | 尝试: {gap.AttemptCount}次 | 创建: {gap.CreatedAt:yyyy-MM-dd HH:mm}");
            if (gap.Resolution is not null)
                sb.AppendLine($"   解决方案: {gap.Resolution}");
            sb.AppendLine();
        }

        return ToolResult.Ok(sb.ToString());
    }

    private async Task<ToolResult> GetStatusAsync(CancellationToken ct)
    {
        var enhanced = await evolutionManager.GetEnhancedStatsAsync(ct);
        var stats = enhanced.Basic;
        var sb = new StringBuilder();
        sb.AppendLine("# 🧬 自我进化状态");
        sb.AppendLine();
        sb.AppendLine($"- 总缺口数: {stats.TotalGaps}");
        sb.AppendLine($"- ✅ 已解决: {stats.Resolved}");
        sb.AppendLine($"- ❌ 失败: {stats.Failed}");
        sb.AppendLine($"- ⏳ 待处理: {stats.Pending}");
        sb.AppendLine($"- 🔧 进行中: {stats.InProgress}");

        if (stats.LastEvolutionAt.HasValue)
            sb.AppendLine($"- 最后进化: {stats.LastEvolutionAt.Value:yyyy-MM-dd HH:mm}");

        if (stats.TotalGaps > 0)
        {
            var rate = (float)stats.Resolved / stats.TotalGaps * 100;
            sb.AppendLine($"- 成功率: {rate:F1}%");
        }

        // 进化度量
        sb.AppendLine();
        sb.AppendLine("## 📊 进化度量");
        sb.AppendLine($"- 验收通过率: {enhanced.AcceptancePassRate:P1}");
        if (enhanced.TotalTokenCost > 0)
        {
            sb.AppendLine($"- 总 Token 消耗: {enhanced.TotalTokenCost:F0}");
            if (enhanced.AvgTokenPerResolved > 0)
                sb.AppendLine($"- 平均每个缺口 Token: {enhanced.AvgTokenPerResolved:F0}");
        }
        if (enhanced.AvgResolutionTime > TimeSpan.Zero)
            sb.AppendLine($"- 平均解决时间: {enhanced.AvgResolutionTime.TotalHours:F1} 小时");

        return ToolResult.Ok(sb.ToString());
    }

    private async Task<ToolResult> ResolveGapAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("gap_id", out var idEl))
            return ToolResult.Error("缺少参数: gap_id",
                errorCode: "missing_gap_id",
                suggestedFix: "传入待规划缺口的 gap_id。",
                retryable: true);

        var gapId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(gapId))
            return ToolResult.Error("gap_id 不能为空");

        var resolution = parameters.TryGetProperty("resolution", out var resEl)
            ? resEl.GetString() ?? "手动标记解决"
            : "手动标记解决";

        var steps = await evolutionManager.GetPlanStepsAsync(gapId, ct);
        if (steps.Count == 0)
            return ToolResult.Error($"缺口 {gapId} 尚无实现计划，不能直接关闭。请先创建计划并完成实施/验收。");

        var unfinished = steps
            .Where(s => s.Status is not PlanStepStatus.Completed and not PlanStepStatus.Skipped)
            .ToList();
        if (unfinished.Count > 0)
        {
            var first = unfinished[0];
            return ToolResult.Error($"缺口 {gapId} 仍有未完成步骤，不能关闭。未完成示例: [{first.Id}] {first.Title} ({first.Status})");
        }

        // 验收门检查 — 所有 Verification 步骤必须通过自动化验证
        var gateResult = await evolutionManager.CheckAcceptanceGateAsync(gapId, ct);
        if (!gateResult.Passed)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"❌ 缺口 {gapId} 未通过验收门，不能关闭。");
            sb.AppendLine(gateResult.Summary);
            if (gateResult.FailedSteps.Count > 0)
            {
                sb.AppendLine("失败步骤:");
                foreach (var f in gateResult.FailedSteps)
                    sb.AppendLine($"  - {f}");
            }
            sb.AppendLine("\n请确保所有 Verification 步骤的验证脚本通过 (exit=0)，或先运行验证。");
            return ToolResult.Error(sb.ToString());
        }

        await evolutionManager.UpdateGapAsync(gapId, EvolutionGapStatus.Resolved, resolution, ct: ct);

        var gateInfo = gateResult.PassedCount > 0
            ? $"\n验收门: {gateResult.Summary}"
            : "";
        return ToolResult.Ok($"✅ 缺口 {gapId} 已标记为已解决。\n解决方案: {resolution}{gateInfo}");
    }

    /// <summary>
    /// 为能力缺口创建树形实现计划
    /// </summary>
    private async Task<ToolResult> PlanGapAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("gap_id", out var idEl))
            return ToolResult.Error("缺少参数: gap_id");

        var gapId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(gapId))
            return ToolResult.Error("gap_id 不能为空",
                errorCode: "invalid_gap_id",
                suggestedFix: "确保 gap_id 是非空字符串。",
                retryable: true);

        if (!TryGetPlanStepsElement(parameters, out var stepsEl, out var parseError))
            return ToolResult.Error(parseError ?? "缺少参数: steps",
                errorCode: "invalid_plan_steps",
                suggestedFix: "使用 steps 数组，或使用兼容参数名 plan；也可传入可解析为 JSON 数组的字符串。",
                retryable: true);

        var steps = ParsePlanSteps(stepsEl);
        if (steps.Count == 0)
            return ToolResult.Error(
                "steps 不能为空，且每个步骤至少需要 title。支持参数: steps(JSON数组) 或 plan(JSON数组/字符串)。",
                errorCode: "empty_plan_steps",
                suggestedFix: "为每个计划步骤提供 title，并确保最外层是非空数组。",
                retryable: true);

        await evolutionManager.CreatePlanAsync(gapId, steps, ct);

        var totalCount = CountSteps(steps);
        var tree = RenderPlanTree(steps, indent: 0);
        logger.LogInformation("[SelfEvolve] Plan created for gap {GapId}: {Count} steps", gapId, totalCount);

        return ToolResult.Ok($"✅ 已为缺口 {gapId} 创建实现计划（{totalCount} 个步骤）\n\n{tree}\n\n使用 self_evolve execute_step 开始逐步执行。");
    }

    private static bool TryGetPlanStepsElement(JsonElement parameters, out JsonElement stepsEl, out string? error)
    {
        if (TryReadPlanStepsProperty(parameters, "steps", out stepsEl, out error))
            return true;

        if (TryReadPlanStepsProperty(parameters, "plan", out stepsEl, out error))
            return true;

        stepsEl = default;
        error = "缺少参数: steps (JSON 数组，每个元素包含 title, description?, hypothesis?, acceptance_criteria?, step_type?, children?)。兼容参数名: plan。";
        return false;
    }

    private static bool TryReadPlanStepsProperty(JsonElement parameters, string propertyName, out JsonElement stepsEl, out string? error)
    {
        error = null;
        stepsEl = default;

        if (!parameters.TryGetProperty(propertyName, out var rawEl))
            return false;

        if (rawEl.ValueKind == JsonValueKind.Array)
        {
            stepsEl = rawEl;
            return true;
        }

        if (rawEl.ValueKind != JsonValueKind.String)
        {
            error = $"参数 {propertyName} 必须是 JSON 数组或可解析为 JSON 数组的字符串。";
            return false;
        }

        var rawText = rawEl.GetString();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            error = $"参数 {propertyName} 不能为空。";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawText);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = $"参数 {propertyName} 解析后必须是 JSON 数组。";
                return false;
            }

            stepsEl = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"参数 {propertyName} 不是合法的 JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 查看缺口的树形实现计划
    /// </summary>
    private async Task<ToolResult> ListPlanAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("gap_id", out var idEl))
            return ToolResult.Error("缺少参数: gap_id");

        var gapId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(gapId))
            return ToolResult.Error("gap_id 不能为空");

        var steps = await evolutionManager.GetPlanStepsAsync(gapId, ct);
        if (steps.Count == 0)
            return ToolResult.Ok($"缺口 {gapId} 尚无实现计划。请先使用 plan_gap 创建计划。");

        var sb = new StringBuilder();
        sb.AppendLine($"# 📋 缺口 {gapId} 实现计划");
        sb.AppendLine();

        // 构建树形渲染
        var rootSteps = steps.Where(s => s.ParentStepId is null).OrderBy(s => s.Order);
        var childMap = steps
            .Where(s => s.ParentStepId is not null)
            .GroupBy(s => s.ParentStepId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Order).ToList());

        RenderStepTree(sb, rootSteps, childMap, indent: 0);

        // 统计
        var completed = steps.Count(s => s.Status == PlanStepStatus.Completed);
        var failed = steps.Count(s => s.Status == PlanStepStatus.Failed);
        var pending = steps.Count(s => s.Status == PlanStepStatus.Pending);
        sb.AppendLine();
        sb.AppendLine($"进度: {completed}/{steps.Count} 完成 | {failed} 失败 | {pending} 待执行");

        // 下一个待执行步骤
        var next = await evolutionManager.GetNextPendingStepAsync(gapId, ct);
        if (next is not null)
            sb.AppendLine($"▶ 下一步: [{next.Id}] {next.Title}");

        return ToolResult.Ok(sb.ToString());
    }

    /// <summary>
    /// 获取下一个待执行的步骤信息
    /// </summary>
    private async Task<ToolResult> ExecuteStepAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("gap_id", out var idEl))
            return ToolResult.Error("缺少参数: gap_id");

        var gapId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(gapId))
            return ToolResult.Error("gap_id 不能为空");

        var steps = await evolutionManager.GetPlanStepsAsync(gapId, ct);
        if (steps.Count == 0)
            return ToolResult.Error($"缺口 {gapId} 尚无实现计划。请先使用 plan_gap 创建计划。");

        var next = await evolutionManager.GetNextPendingStepAsync(gapId, ct);
        if (next is null)
        {
            return ToolResult.Ok($"缺口 {gapId} 的计划步骤已全部完成，当前应进入验收阶段。请由验收任务执行验证，通过后再 resolve_gap。");
        }

        // 标记为 InProgress
        await evolutionManager.UpdateStepAsync(next.Id, PlanStepStatus.InProgress, ct: ct);

        // 根据步骤类型推进缺口阶段：verification 步骤进入 Verifying，其余进入 Implementing
        var gapStatus = next.StepType == PlanStepType.Verification
            ? EvolutionGapStatus.Verifying
            : EvolutionGapStatus.Implementing;
        await evolutionManager.UpdateGapAsync(gapId, gapStatus, ct: ct);

        var typeLabel = next.StepType switch
        {
            PlanStepType.Diagnostic => "🔍 诊断/分析",
            PlanStepType.Verification => "🧪 验证/测试",
            _ => "🔧 实现",
        };

        var sb = new StringBuilder();
        sb.AppendLine($"# ▶ 当前步骤");
        sb.AppendLine($"- ID: {next.Id}");
        sb.AppendLine($"- 标题: {next.Title}");
        sb.AppendLine($"- 类型: {typeLabel}");
        sb.AppendLine($"- 深度: {next.Depth} ({"".PadLeft(next.Depth, '─')}级子任务)");
        if (!string.IsNullOrEmpty(next.Hypothesis))
            sb.AppendLine($"- 假设: {next.Hypothesis}");
        if (!string.IsNullOrEmpty(next.AcceptanceCriteria))
            sb.AppendLine($"- 验收标准: {next.AcceptanceCriteria}");
        if (!string.IsNullOrEmpty(next.VerificationScript))
            sb.AppendLine($"- 自动验证脚本: `{next.VerificationScript}` (complete_step 时自动执行)");
        if (!string.IsNullOrEmpty(next.Checkpoint))
            sb.AppendLine($"- 📌 检查点: {next.Checkpoint} (从此处恢复执行)");
        if (!string.IsNullOrEmpty(next.Description))
            sb.AppendLine($"- 描述: {next.Description}");
        sb.AppendLine();
        sb.AppendLine("请执行此步骤，完成后调用 self_evolve complete_step 标记结果。确保满足验收标准后再标记为完成。");
        if (!string.IsNullOrEmpty(next.VerificationScript))
            sb.AppendLine("⚠ 此步骤配有自动验证脚本，complete_step 时会自动运行验证。");

        logger.LogInformation("[SelfEvolve] Executing step {StepId}: {Title}", next.Id, next.Title);
        return ToolResult.Ok(sb.ToString());
    }

    /// <summary>
    /// 标记步骤完成或失败 — Verification 步骤自动运行验证脚本
    /// </summary>
    private async Task<ToolResult> CompleteStepAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("step_id", out var idEl))
            return ToolResult.Error("缺少参数: step_id");

        var stepId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(stepId))
            return ToolResult.Error("step_id 不能为空");

        var success = !parameters.TryGetProperty("success", out var successEl)
            || successEl.GetBoolean();

        var result = parameters.TryGetProperty("result", out var resEl)
            ? resEl.GetString() ?? "" : "";

        // 保存检查点
        var checkpoint = parameters.TryGetProperty("checkpoint", out var cpEl)
            ? cpEl.GetString() : null;
        if (!string.IsNullOrWhiteSpace(checkpoint))
            await evolutionManager.SaveStepCheckpointAsync(stepId, checkpoint, ct);

        // 获取步骤详情以检查是否需要自动化验证
        var step = await evolutionManager.GetStepAsync(stepId, ct);

        // Verification 步骤如果有验证脚本，自动运行
        if (success && step?.VerificationScript is { Length: > 0 } script)
        {
            var (exitCode, output) = await RunVerificationScriptAsync(script, ct);
            await evolutionManager.UpdateStepVerificationAsync(stepId, exitCode, output, ct);

            if (exitCode != 0)
            {
                // 验证脚本失败 — 覆盖 success
                success = false;
                result = $"自动化验证失败 (exit={exitCode}): {Truncate(output ?? "", 300)}\n原始结果: {result}";
                logger.LogWarning("[SelfEvolve] Step {StepId} verification script failed: exit={ExitCode}", stepId, exitCode);
            }
            else
            {
                result = $"✅ 自动化验证通过 (exit=0)\n{result}";
                logger.LogInformation("[SelfEvolve] Step {StepId} verification script passed", stepId);
            }
        }

        var status = success ? PlanStepStatus.Completed : PlanStepStatus.Failed;
        await evolutionManager.UpdateStepAsync(stepId, status, result, ct);

        var icon = success ? "✅" : "❌";
        logger.LogInformation("[SelfEvolve] Step {StepId} {Status}: {Result}",
            stepId, status, Truncate(result, 100));

        return ToolResult.Ok(success
            ? $"{icon} 步骤 {stepId} 已标记为完成。\n结果: {result}\n\n继续调用 execute_step 获取下一个步骤；若已无步骤，则进入验收阶段。"
            : $"{icon} 步骤 {stepId} 已标记为失败。\n结果: {result}\n\n对应缺口已回到失败/待修复状态，需后续重新执行实现步骤。");
    }

    /// <summary>
    /// 运行验证脚本并返回退出码和输出
    /// </summary>
    private async Task<(int ExitCode, string? Output)> RunVerificationScriptAsync(string script, CancellationToken ct)
    {
        // 优先使用 Docker 沙箱执行验证脚本
        if (IsDockerAvailable())
        {
            logger.LogInformation("[SelfEvolve] Running verification in Docker: {Script}", script);
            return await DockerRunVerificationAsync(script, FindWorkspacePath(), ct);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? $"/c {script}" : $"-c \"{script.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动验证进程");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var output = string.IsNullOrWhiteSpace(stderr)
                ? stdout.TrimEnd()
                : $"{stdout.TrimEnd()}\nSTDERR: {stderr.TrimEnd()}";

            return (proc.ExitCode, Truncate(output, 500));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SelfEvolve] Verification script execution failed: {Script}", script);
            return (-1, $"脚本执行异常: {ex.Message}");
        }
    }

    private static List<PlanStepInput> ParsePlanSteps(JsonElement stepsEl)
    {
        var result = new List<PlanStepInput>();
        foreach (var stepEl in stepsEl.EnumerateArray())
        {
            var title = stepEl.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) continue;

            var desc = stepEl.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
            var hypothesis = stepEl.TryGetProperty("hypothesis", out var hEl) ? hEl.GetString() ?? "" : "";
            var ac = stepEl.TryGetProperty("acceptance_criteria", out var acEl) ? acEl.GetString() ?? "" : "";
            var vs = stepEl.TryGetProperty("verification_script", out var vsEl) ? vsEl.GetString() : null;
            var stepType = stepEl.TryGetProperty("step_type", out var stEl) ? stEl.GetString() ?? "implementation" : "implementation";
            var children = stepEl.TryGetProperty("children", out var cEl) && cEl.ValueKind == JsonValueKind.Array
                ? ParsePlanSteps(cEl)
                : [];

            result.Add(new PlanStepInput
            {
                Title = title,
                Description = desc,
                Hypothesis = hypothesis,
                AcceptanceCriteria = ac,
                VerificationScript = vs,
                StepType = stepType,
                Children = children,
            });
        }
        return result;
    }

    private static int CountSteps(List<PlanStepInput> steps)
        => steps.Sum(s => 1 + CountSteps(s.Children));

    private static string RenderPlanTree(List<PlanStepInput> steps, int indent)
    {
        var sb = new StringBuilder();
        var prefix = new string(' ', indent * 2);
        foreach (var step in steps)
        {
            var typeIcon = step.StepType?.ToLowerInvariant() switch
            {
                "diagnostic" or "diagnosis" or "analysis" => "🔍",
                "verification" or "verify" or "test" => "🧪",
                _ => "🔧",
            };
            sb.AppendLine($"{prefix}├─ {typeIcon} {step.Title}");
            if (!string.IsNullOrEmpty(step.Hypothesis))
                sb.AppendLine($"{prefix}   假设: {step.Hypothesis}");
            if (!string.IsNullOrEmpty(step.AcceptanceCriteria))
                sb.AppendLine($"{prefix}   验收: {step.AcceptanceCriteria}");
            if (!string.IsNullOrEmpty(step.VerificationScript))
                sb.AppendLine($"{prefix}   🔬 验证脚本: `{step.VerificationScript}`");
            if (!string.IsNullOrEmpty(step.Description))
                sb.AppendLine($"{prefix}   {step.Description}");
            if (step.Children.Count > 0)
                sb.Append(RenderPlanTree(step.Children, indent + 1));
        }
        return sb.ToString();
    }

    private static void RenderStepTree(
        StringBuilder sb,
        IEnumerable<EvolutionPlanStep> steps,
        Dictionary<string, List<EvolutionPlanStep>> childMap,
        int indent)
    {
        var prefix = new string(' ', indent * 2);
        foreach (var step in steps)
        {
            var statusIcon = step.Status switch
            {
                PlanStepStatus.Completed => "✅",
                PlanStepStatus.Failed => "❌",
                PlanStepStatus.InProgress => "⏳",
                PlanStepStatus.Skipped => "⏭️",
                _ => "⬜",
            };
            var typeIcon = step.StepType switch
            {
                PlanStepType.Diagnostic => "🔍",
                PlanStepType.Verification => "🧪",
                _ => "🔧",
            };
            sb.AppendLine($"{prefix}{statusIcon} {typeIcon} [{step.Id}] {step.Title}");
            if (!string.IsNullOrEmpty(step.AcceptanceCriteria))
                sb.AppendLine($"{prefix}   验收: {step.AcceptanceCriteria}");
            if (!string.IsNullOrEmpty(step.Result))
                sb.AppendLine($"{prefix}   └─ {Truncate(step.Result, 100)}");

            if (childMap.TryGetValue(step.Id, out var children))
                RenderStepTree(sb, children, childMap, indent + 1);
        }
    }

    /// <summary>
    /// 创建技能 — 先写入 staging 目录，验收通过后原子部署到正式目录
    /// </summary>
    private Task<ToolResult> CreateSkillAsync(JsonElement parameters, CancellationToken ct)
    {
        _ = ct; // 纯文件操作，无需异步取消

        if (!parameters.TryGetProperty("name", out var nameEl))
            return Task.FromResult(ToolResult.Error("缺少参数: name (技能英文名，如 json_converter)"));
        var name = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Error("name 不能为空"));

        if (!parameters.TryGetProperty("description", out var descEl))
            return Task.FromResult(ToolResult.Error("缺少参数: description (技能描述)"));
        var description = descEl.GetString() ?? "";

        var displayName = parameters.TryGetProperty("display_name", out var dnEl)
            ? dnEl.GetString() ?? name : name;

        var content = parameters.TryGetProperty("content", out var contentEl)
            ? contentEl.GetString() ?? "" : "";

        // Skills 目录 = 运行目录/Skills/
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "Skills");
        var sanitized = SanitizeFileName(name);
        var skillDir = Path.Combine(skillsRoot, sanitized);
        var stagingDir = Path.Combine(skillsRoot, ".staging", sanitized);

        if (Directory.Exists(skillDir))
            return Task.FromResult(ToolResult.Error($"技能 '{name}' 已存在于 {skillDir}，请先删除或更换名称。"));

        try
        {
            // 先写入 staging 目录
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            // 写入 skill.json 清单 (使用 JsonObject 避免反射序列化)
            var manifest = new JsonObject
            {
                ["name"] = name,
                ["displayName"] = displayName,
                ["description"] = description,
                ["version"] = "1.0.0",
                ["source"] = "evolution",
                ["status"] = "trial",
                ["usageCount"] = 0,
                ["successCount"] = 0,
                ["importedAt"] = DateTimeOffset.Now.ToString("o"),
            };
            File.WriteAllText(
                Path.Combine(stagingDir, "skill.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // 写入 SKILL.md
            if (!string.IsNullOrWhiteSpace(content))
                File.WriteAllText(Path.Combine(stagingDir, "SKILL.md"), content);

            // 创建 scripts/ 子目录
            var scriptsDir = Path.Combine(stagingDir, "scripts");
            Directory.CreateDirectory(scriptsDir);

            // 如果提供了脚本内容，写入对应文件
            if (parameters.TryGetProperty("scripts", out var scriptsEl) && scriptsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var script in scriptsEl.EnumerateObject())
                {
                    var scriptName = SanitizeFileName(script.Name);
                    var scriptContent = script.Value.GetString() ?? "";
                    File.WriteAllText(Path.Combine(scriptsDir, scriptName), scriptContent);
                }
            }

            // 从 staging 原子部署到正式目录
            Directory.Move(stagingDir, skillDir);

            logger.LogInformation("[SelfEvolve] Skill created: {Name} at {Dir} (via staging)", name, skillDir);
            return Task.FromResult(ToolResult.Ok(
                $"✅ 技能 '{displayName}' 已创建（试用期）\n" +
                $"📁 路径: {skillDir}\n" +
                $"📄 skill.json + SKILL.md + scripts/ 已就绪\n" +
                $"🔬 状态: trial — 积累使用数据后自动转为 stable\n" +
                $"技能将在下次加载时自动可用。"));
        }
        catch (Exception ex)
        {
            // 清理 staging
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            logger.LogError(ex, "[SelfEvolve] Failed to create skill: {Name}", name);
            return Task.FromResult(ToolResult.Error($"创建技能失败: {ex.Message}"));
        }
    }

    /// <summary>
    /// 部署构建 — 先创建文件快照用于回滚，在工作区执行 dotnet build，失败时自动恢复
    /// </summary>
    private async Task<ToolResult> DeployBuildAsync(JsonElement parameters, CancellationToken ct)
    {
        var workspace = parameters.TryGetProperty("workspace", out var wsEl)
            ? wsEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(workspace))
            workspace = FindWorkspacePath();

        if (string.IsNullOrWhiteSpace(workspace))
            return ToolResult.Error("未找到工作区路径，请传入 workspace 参数或设置 OWNERAI_WORKSPACE 环境变量。");

        var outDir = AppContext.BaseDirectory;
        var snapshotDir = Path.Combine(outDir, ".evolution-snapshot");

        // 创建构建前快照 — 用于回滚
        var beforeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(snapshotDir))
                Directory.Delete(snapshotDir, true);
            Directory.CreateDirectory(snapshotDir);

            foreach (var f in Directory.EnumerateFiles(outDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(f);
                if (ext is ".dll" or ".exe" or ".pdb")
                {
                    var fileName = Path.GetFileName(f);
                    beforeFiles.Add(fileName);
                    File.Copy(f, Path.Combine(snapshotDir, fileName), overwrite: true);
                }
            }

            logger.LogInformation("[SelfEvolve] deploy_build: snapshot created ({Count} files)", beforeFiles.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SelfEvolve] Failed to create snapshot (continuing without rollback protection)");
        }

        // 执行 dotnet build（优先 Docker 沙箱，不可用时回退到直接执行）
        var useDocker = IsDockerAvailable();
        logger.LogInformation("[SelfEvolve] deploy_build: building {Workspace} → {OutDir} (Docker={Docker})", workspace, outDir, useDocker);

        var sb = new StringBuilder();
        int exitCode;

        try
        {
            if (useDocker)
            {
                var (code, output) = await DockerBuildAsync(workspace, ct);
                exitCode = code;
                if (!string.IsNullOrWhiteSpace(output)) sb.AppendLine(output);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "build --nologo --verbosity quiet",
                    WorkingDirectory = workspace,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("无法启动 dotnet build 进程");
                var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
                var stderr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                exitCode = proc.ExitCode;

                if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine(stdout.TrimEnd());
                if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine(stderr.TrimEnd());
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"构建进程执行失败: {ex.Message}");
        }

        if (exitCode != 0)
        {
            logger.LogWarning("[SelfEvolve] deploy_build failed (exit {Code}), rolling back...", exitCode);

            // 构建失败 — 自动回滚
            var rollbackResult = RollbackFromSnapshot(snapshotDir, outDir);
            return ToolResult.Error(
                $"❌ 构建失败 (exit code {exitCode})\n{Truncate(sb.ToString(), 500)}\n\n" +
                $"🔄 回滚: {rollbackResult}");
        }

        // 构建成功 — 对比文件快照，清理孤立产物
        var afterFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(outDir, "*.*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(f);
            if (ext is ".dll" or ".exe" or ".pdb")
                afterFiles.Add(Path.GetFileName(f));
        }

        var orphaned = new List<string>();
        foreach (var old in beforeFiles)
        {
            if (!afterFiles.Contains(old))
            {
                var fullPath = Path.Combine(outDir, old);
                try
                {
                    File.Delete(fullPath);
                    orphaned.Add(old);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[SelfEvolve] Could not delete orphaned file: {File}", old);
                }
            }
        }

        // 构建成功 — 清理快照
        try { if (Directory.Exists(snapshotDir)) Directory.Delete(snapshotDir, true); } catch { }

        var result = new StringBuilder();
        result.AppendLine($"✅ 构建成功，已部署到 {outDir}" + (useDocker ? " (Docker 沙箱)" : ""));
        result.AppendLine($"📦 当前产物: {afterFiles.Count} 个文件");
        if (orphaned.Count > 0)
        {
            result.AppendLine($"🗑️ 已清理 {orphaned.Count} 个旧文件:");
            foreach (var f in orphaned) result.AppendLine($"   - {f}");
        }
        if (sb.Length > 0)
            result.AppendLine($"\n构建输出:\n{Truncate(sb.ToString(), 300)}");

        logger.LogInformation("[SelfEvolve] deploy_build succeeded. {Orphaned} orphaned files cleaned.", orphaned.Count);
        return ToolResult.Ok(result.ToString());
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "...");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid));
    }

    /// <summary>
    /// 从快照恢复 — 构建失败时回滚
    /// </summary>
    private string RollbackFromSnapshot(string snapshotDir, string outDir)
    {
        if (!Directory.Exists(snapshotDir))
            return "无快照可恢复";

        var restored = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(snapshotDir))
            {
                var dest = Path.Combine(outDir, Path.GetFileName(f));
                File.Copy(f, dest, overwrite: true);
                restored++;
            }
            Directory.Delete(snapshotDir, true);
            logger.LogInformation("[SelfEvolve] Rollback completed: {Count} files restored", restored);
            return $"已恢复 {restored} 个文件";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SelfEvolve] Rollback failed");
            return $"回滚失败: {ex.Message}";
        }
    }

    private static string? FindWorkspacePath()
    {
        var envPath = Environment.GetEnvironmentVariable("OWNERAI_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath)) return envPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    // ── Docker 沙箱支持 ──

    private static bool? s_dockerAvailable;

    /// <summary>检测 Docker 是否可用</summary>
    private static bool IsDockerAvailable()
    {
        if (s_dockerAvailable.HasValue) return s_dockerAvailable.Value;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format '{{.ServerVersion}}'",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            s_dockerAvailable = proc?.ExitCode == 0;
        }
        catch
        {
            s_dockerAvailable = false;
        }
        return s_dockerAvailable.Value;
    }

    /// <summary>在 Docker 容器中执行 dotnet build（沙箱隔离）</summary>
    private static async Task<(int ExitCode, string Output)> DockerBuildAsync(string workspace, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"run --rm -v \"{workspace}:/src\" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet build --nologo --verbosity quiet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 Docker 进程");

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var output = string.IsNullOrWhiteSpace(stderr)
            ? stdout.TrimEnd()
            : $"{stdout.TrimEnd()}\nSTDERR: {stderr.TrimEnd()}";

        return (proc.ExitCode, output);
    }

    /// <summary>在 Docker 容器中执行验证脚本（沙箱隔离）</summary>
    private static async Task<(int ExitCode, string? Output)> DockerRunVerificationAsync(string script, string? workDir, CancellationToken ct)
    {
        var volumeMount = !string.IsNullOrWhiteSpace(workDir)
            ? $"-v \"{workDir}:/work\" -w /work"
            : "";

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"run --rm {volumeMount} mcr.microsoft.com/dotnet/sdk:10.0 sh -c \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 Docker 进程");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var output = string.IsNullOrWhiteSpace(stderr)
                ? stdout.TrimEnd()
                : $"{stdout.TrimEnd()}\nSTDERR: {stderr.TrimEnd()}";

            return (proc.ExitCode, Truncate(output, 500));
        }
        catch (Exception ex)
        {
            return (-1, $"Docker 脚本执行异常: {ex.Message}");
        }
    }
}
