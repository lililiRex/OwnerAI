using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwnerAI.Agent;
using OwnerAI.Agent.Scheduler;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Evolution;

/// <summary>
/// 循环执行的自我进化后台服务
/// </summary>
public sealed class EvolutionBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EvolutionBackgroundService> _logger;
    private readonly string _workspacePath;

    public bool IsEvolving { get; private set; }
    public string CurrentPhase { get; private set; } = "就绪";

    /// <summary>进化者人设</summary>
    private const string EvolutionPersona =
        "你是 OwnerAI 的自我进化模块。你的使命是不断增强系统能力 — 分析能力缺口，编写高质量代码，实现新工具，让 OwnerAI 变得更强大。你具有完整的文件读写、命令执行、网络搜索能力。你编写的代码必须遵循项目规范，通过编译验证。";

    public EvolutionBackgroundService(
        IServiceProvider services,
        ILogger<EvolutionBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
        _workspacePath = FindWorkspacePath() ?? "";
    }

    /// <summary>
    /// 手动触发进化 — 直接让后台循环执行
    /// </summary>
    public void TriggerNow()
    {
        // For simplicity, we can let the loop handle it
        _logger.LogInformation("[Evolution] Manual trigger received... ");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_workspacePath))
        {
            _logger.LogWarning("[Evolution] ⚠ 未找到工作区 (.sln)，跳过后台进化任务。" +
                "如需自我进化功能，请从源码目录启动应用或设置 OWNERAI_WORKSPACE 环境变量。");
            return;
        }

        _logger.LogInformation("[Evolution] 🧬 Background service started. Scanning permanently.");

        // Wait to not slow down host startup
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { return; }

        // 在独立线程中运行进化循环
        await Task.Factory.StartNew(
            () => EvolutionLoopAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    private async Task EvolutionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DoEvolutionCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Evolution] Cycle failed.");
                await SetStatusAsync($"能力形成失败: {ex.Message}", false, stoppingToken);
                await PublishChatMessageAsync(BackgroundTaskPhase.Failed,
                    $"❌ 自我进化执行失败：{Truncate(ex.Message, 120)}", stoppingToken);
            }

            // Continuously verify and scan in background
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); } catch { break; }
        }
    }

    private async Task DoEvolutionCycleAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var cycleTaskId = $"evolution-{Guid.NewGuid().ToString("N")[..12]}";

        // Check pending gaps
        var gapManager = scope.ServiceProvider.GetService<IEvolutionManager>();
        if (gapManager != null)
        {
            var gaps = await gapManager.ListGapsAsync(EvolutionGapStatus.Detected, ct);
            if (gaps.Count > 0)
            {
                var sourceCount = gaps.Count(g => g.Category == "source");
                var skillCount = gaps.Count(g => g.Category == "skill");
                var taskDesc = $"修复 {gaps.Count} 个能力缺口 (💻源码:{sourceCount} 🧩技能:{skillCount})";

                await SetStatusAsync("能力形成中...", true, ct);
                await PublishChatMessageAsync(BackgroundTaskPhase.Start,
                    $"🧬 自我进化开始：发现 {taskDesc}，正在修复...", ct, taskId: cycleTaskId);

                await RunAgentWithLockAsync(BuildGapFixPrompt(), scope.ServiceProvider, cycleTaskId, ct);

                await PublishChatMessageAsync(BackgroundTaskPhase.Completed,
                    $"✅ 自我进化完成：{taskDesc}", ct, taskId: cycleTaskId);
            }
            else
            {
                // Scan periodically
                await SetStatusAsync("自我审视缺口...", true, ct);
                await PublishChatMessageAsync(BackgroundTaskPhase.Start,
                    "🔍 自我进化开始：正在扫描能力缺口...", ct, taskId: cycleTaskId);

                await RunAgentWithLockAsync(BuildSelfScanPrompt(), scope.ServiceProvider, cycleTaskId, ct);

                await PublishChatMessageAsync(BackgroundTaskPhase.Completed,
                    "✅ 自我进化完成：能力缺口扫描已结束", ct, taskId: cycleTaskId);
            }
        }

        await SetStatusAsync("就绪", false, ct);
    }

    /// <summary>
    /// 获取 LLM 互斥锁后执行 Agent — 与 SchedulerService 共享锁，避免并发 LLM 访问
    /// </summary>
    private async Task RunAgentWithLockAsync(string prompt, IServiceProvider sp, string taskId, CancellationToken ct)
    {
        var scheduler = _services.GetService<SchedulerService>();
        if (scheduler is not null)
        {
            using var llmLock = await scheduler.AcquireLlmLockAsync(markUserActive: false, ct);
            await RunAgentAsync(prompt, sp, taskId, ct);
        }
        else
        {
            // 无调度器时直接执行
            await RunAgentAsync(prompt, sp, taskId, ct);
        }
    }

    private async Task RunAgentAsync(string prompt, IServiceProvider sp, string taskId, CancellationToken ct)
    {
        var agent = sp.GetRequiredService<IAgent>();

        var config = new AgentConfig { Persona = EvolutionPersona, Temperature = 0.3f, DefaultModel = "deepseek-reasoner", FallbackModel = "gpt-4o" };
        var context = new AgentContext
        {
            SessionId = "evolution-" + Guid.NewGuid().ToString("N")[..8],
            UserMessage = prompt,
            Config = config,
            Role = AgentRole.Evolution,
            WorkCategory = ModelWorkCategory.EvolutionExecution,
        };

        var responseBuilder = new System.Text.StringBuilder();

        try
        {
            await foreach (var chunk in agent.ExecuteAsync(context, ct))
            {
                if (chunk.Text is { Length: > 0 })
                    responseBuilder.Append(chunk.Text);

                // 工具调用时发送进度消息
                if (chunk.ToolCall is not null)
                {
                    var status = chunk.ToolCall.Success ? "✅" : "❌";
                    await PublishChatMessageAsync(BackgroundTaskPhase.Progress,
                        $"🔧 进化中调用工具 [{chunk.ToolCall.ToolName}] {status} ({chunk.ToolCall.Duration.TotalSeconds:F1}s)", ct, taskId: taskId);
                }
            }

            var finalResponse = responseBuilder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(finalResponse))
            {
                await PublishChatMessageAsync(BackgroundTaskPhase.Progress,
                    finalResponse,
                    ct,
                    taskId: taskId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent execution error during evolution");
            throw;
        }
    }

    private async Task SetStatusAsync(string phase, bool isActive, CancellationToken ct)
    {
        IsEvolving = isActive;
        CurrentPhase = phase;

        // Note: EventBus instance requires creating outer scope or from services directly
        var eventBus = _services.GetService<IEventBus>();
        if (eventBus != null)
        {
            try
            {
                await eventBus.PublishAsync(new EvolutionStatusEvent
                {
                    Phase = isActive ? $"🌟 {phase}" : phase,
                    IsActive = isActive
                }, ct);
            } 
            catch { }
        }
    }

    private async Task PublishChatMessageAsync(BackgroundTaskPhase phase, string message, CancellationToken ct, string? taskId = null)
    {
        var eventBus = _services.GetService<IEventBus>();
        if (eventBus is null) return;

        try
        {
            await eventBus.PublishAsync(new BackgroundTaskChatEvent
            {
                Source = "evolution",
                Phase = phase,
                Message = message,
                TaskName = "自我进化",
                TaskId = taskId,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Evolution] Failed to publish chat message (non-critical)");
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "...");

    private string BuildSelfScanPrompt()
    {
        var skillsDir = Path.Combine(AppContext.BaseDirectory, "Skills");
        return $"""
            你正在执行自我进化扫描。请执行以下步骤:

            1. 使用 list_directory 查看项目工具目录: {_workspacePath}/src/OwnerAI.Agent.Tools/
            2. 使用 list_directory 查看已有技能目录: {skillsDir}
            3. 使用 read_file 快速浏览几个工具的实现，了解当前工具能力
            4. 思考以下方面是否存在能力缺口:
               - 是否缺少常用的文件操作工具（如文件压缩、批量重命名）
               - 是否缺少数据处理工具（如 JSON/CSV 转换、数据统计）
               - 是否缺少网络相关工具（如 API 调用、邮件发送）
               - 现有工具的功能是否完整
               - 是否需要新的技能（Skill）来扩展知识
            5. 如果发现能力缺口，使用 self_evolve report_gap 记录，必须指定 category 参数:
               - category="source" → 需要修改源码的能力缺口（新工具、核心功能增强）
               - category="skill" → 可通过技能文件实现的能力缺口（知识型、脚本型、配置型）

            注意:
            - 只报告**确实缺少且实用的**能力，不要报告边缘需求
            - 优先使用 skill 类别，仅在必须修改 C# 源码时才使用 source 类别
            """;
    }

    private string BuildGapFixPrompt()
    {
        var skillsDir = Path.Combine(AppContext.BaseDirectory, "Skills");
        return $"""
            你正在执行自我进化任务。请按以下步骤操作:

            1. 使用 self_evolve list_gaps 查看待解决的能力缺口
            2. 如果没有待解决的缺口，直接回复"系统能力完整"
            3. 如果有缺口，选择优先级最高的一项，根据其 **类别(category)** 采取不同策略:

            ## 🧩 技能进化 (category=skill)
            使用 self_evolve create_skill 在技能文件夹中创建:
            - 传入 name, display_name, description, content (SKILL.md 内容)
            - 技能会自动放在 {skillsDir}/<技能名>/ 目录
            - 遵循 OpenClaw 格式: skill.json + SKILL.md + scripts/
            - **禁止**在源码目录创建技能文件

            ## 💻 源码进化 (category=source)
            - 源码路径: {_workspacePath}
            - 工具目录: {_workspacePath}/src/OwnerAI.Agent.Tools/
            - 通过 read_file 和 write_file 修改源码
            - 修改完成后，使用 self_evolve deploy_build 构建并部署:
              - 自动执行 dotnet build，将产物输出到运行目录
              - 自动清理旧版本的孤立文件 (dll/pdb)
            - **必须**在 deploy_build 成功后才能标记 resolve_gap

            4. 完成后使用 self_evolve resolve_gap 标记已解决
            """;
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

        var appDir = new DirectoryInfo(AppContext.BaseDirectory);
        var parent = appDir.Parent;
        if (parent is not null)
        {
            foreach (var sibling in parent.GetDirectories())
            {
                if (string.Equals(sibling.FullName.TrimEnd(Path.DirectorySeparatorChar), appDir.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                if (sibling.GetFiles("*.sln").Length > 0) return sibling.FullName;
            }
        }
        return null;
    }
}
