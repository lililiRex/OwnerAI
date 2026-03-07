using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Agent.Providers;
using OwnerAI.Configuration;
using OwnerAI.Shared;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Scheduler;

/// <summary>
/// 统一调度服务 — 管理所有后台任务，保证 LLM 并发安全
/// <para>
/// 核心机制:
/// - SemaphoreSlim(1,1) LLM 互斥锁 — 保证同一时间只有一个 LLM 调用（用户对话 或 后台任务）
/// - 双线程调度 — 进化任务和定时任务分别在独立线程中运行，共享 LLM 互斥锁
/// - 用户空闲检测 — 用户活跃时暂停后台任务，空闲 2 分钟后恢复
/// - 优先级队列 — 高优先级任务优先执行
/// - 自动重试 — 失败任务按 MaxRetries 重试
/// - 循环任务自动续期 — 完成后计算下次执行时间并重新入队
/// - 聊天窗口汇报 — 每次触发、执行过程、执行结果均通过消息窗口输出
/// </para>
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IScheduledTaskManager _taskManager;
    private readonly ILogger<SchedulerService> _logger;

    /// <summary>LLM 互斥锁 — 全局唯一，用户对话和后台任务共享</summary>
    private readonly SemaphoreSlim _llmMutex = new(1, 1);

    /// <summary>手动触发通道 — 外部可通过 TriggerNow() 唤醒调度循环</summary>
    private readonly Channel<bool> _triggerChannel = Channel.CreateBounded<bool>(1);

    /// <summary>进化线程触发通道</summary>
    private readonly Channel<bool> _evolutionTriggerChannel = Channel.CreateBounded<bool>(1);

    /// <summary>定时任务线程触发通道</summary>
    private readonly Channel<bool> _scheduledTriggerChannel = Channel.CreateBounded<bool>(1);

    /// <summary>用户最后活跃时间</summary>
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;

    /// <summary>用户空闲阈值 — 空闲超过此时间才执行后台任务</summary>
    private static readonly TimeSpan UserIdleThreshold = TimeSpan.FromMinutes(2);

    /// <summary>启动延迟 — 等待主服务完全初始化</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>进化线程轮询间隔</summary>
    private static readonly TimeSpan EvolutionPollInterval = TimeSpan.FromMinutes(10);

    /// <summary>定时任务线程轮询间隔</summary>
    private static readonly TimeSpan ScheduledPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>进化任务来源标签</summary>
    private const string EvolutionSource = "evolution";

    private const string EvolutionPlanningTaskName = "进化检查";
    private const string EvolutionExecutionTaskName = "进化执行";
    private const string EvolutionVerificationTaskName = "进化验收";

    /// <summary>单个任务最大执行时间</summary>
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(10);

    /// <summary>任务被占用/用户活跃时的延后重试间隔</summary>
    private static readonly TimeSpan DeferredRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>AI 明确表示“本轮未完成”时的继续执行间隔</summary>
    private static readonly TimeSpan IncompleteRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>非进化任务追加的能力缺口反馈指令 — 形成 "执行 → 发现缺口 → 进化" 闭环</summary>
    private const string GapReportingInstruction = """


        ---
        ## ⚠️ 能力缺口反馈（重要）
        在执行上述任务过程中，如果你发现：
        - 缺少某个工具或技能（如无法生成图片、无法操作视频、无法调用某个 API 等）
        - 某个工具执行失败且原因是系统能力不足
        - 需要安装软件/库但当前未安装
        - 任何阻碍任务完成的能力缺陷

        请在完成任务分析后，使用 self_evolve report_gap 逐一报告每个缺失的能力。
        参数示例：
        ```json
        {"action":"report_gap","description":"缺少 AI 图像生成能力，无法调用 Stable Diffusion API 生成漫画分镜","priority":4,"category":"skill","source":"task_feedback"}
        ```
        - priority: 1-5，5 最高。影响任务完成的核心能力设为 4-5
        - category: "skill"（运行时技能）或 "source"（需要修改源码）
        - source: 固定填 "task_feedback"

        即使任务本身无法完成，也请完成能力缺口的报告 — 这将驱动系统自我进化。
        """;

    /// <summary>后台任务统一结束协议 — 由 AI 明确声明本轮是完成、未完成还是失败</summary>
    private const string TaskStatusInstruction = """


        ---
        ## ✅ 本轮任务结束状态（必须）
        在你的最终回复最后一行，必须且只能输出以下三种标记之一：
        - [TASK_STATUS:COMPLETED]  → 本轮任务目标已经完成，可以结束本轮
        - [TASK_STATUS:INCOMPLETE] → 本轮尚未完成，需要后续轮次继续，不应算作失败
        - [TASK_STATUS:FAILED]     → 本轮发生不可恢复错误，应该按失败处理

        判定原则：
        - 只要本轮还有关键步骤未完成、工具调用参数不正确、调用失败但可重试、需要下一轮继续，就使用 [TASK_STATUS:INCOMPLETE]
        - 只有本轮目标真实完成时，才使用 [TASK_STATUS:COMPLETED]
        - 只有出现明确不可恢复错误时，才使用 [TASK_STATUS:FAILED]
        """;

    private int _activeTaskExecutions;

    /// <summary>当前是否正在执行后台任务</summary>
    public bool IsRunningTask { get; private set; }

    /// <summary>当前执行阶段</summary>
    public string CurrentPhase { get; private set; } = "就绪";

    public SchedulerService(
        IServiceProvider services,
        IScheduledTaskManager taskManager,
        ILogger<SchedulerService> logger)
    {
        _services = services;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// 获取 LLM 互斥锁。
    /// 用户对话场景可标记用户活跃；后台服务共享锁时不应更新用户活跃时间。
    /// </summary>
    public async Task<IDisposable> AcquireLlmLockAsync(bool markUserActive = true, CancellationToken ct = default)
    {
        if (markUserActive)
            _lastUserActivity = DateTimeOffset.Now;

        await _llmMutex.WaitAsync(ct);

        if (markUserActive)
            _lastUserActivity = DateTimeOffset.Now;

        return new LlmLockHandle(_llmMutex, this, markUserActive);
    }

    /// <summary>
    /// 标记用户活跃 — 每次用户发消息时调用
    /// </summary>
    public void NotifyUserActive()
    {
        _lastUserActivity = DateTimeOffset.Now;
    }

    /// <summary>
    /// 手动触发一轮调度（唤醒两个线程）
    /// </summary>
    public void TriggerNow()
    {
        _triggerChannel.Writer.TryWrite(true);
        _evolutionTriggerChannel.Writer.TryWrite(true);
        _scheduledTriggerChannel.Writer.TryWrite(true);
        _logger.LogInformation("[Scheduler] Manual trigger received");
    }

    /// <summary>
    /// 立即执行指定任务 — 将任务状态设为 Pending、NextRunAt 设为当前时间，并唤醒对应调度线程
    /// </summary>
    public async Task RunTaskNowAsync(string taskId, CancellationToken ct = default)
    {
        var task = await _taskManager.GetTaskAsync(taskId, ct);
        if (task is null)
        {
            _logger.LogWarning("[Scheduler] RunTaskNow: task {Id} not found", taskId);
            return;
        }

        // 将任务设为 Pending 并设置 NextRunAt 为当前时间，使其立即就绪
        await _taskManager.UpdateTaskAsync(taskId, ScheduledTaskStatus.Pending,
            nextRunAt: DateTimeOffset.Now, ct: ct);

        _logger.LogInformation("[Scheduler] RunTaskNow: {Id} — {Name} queued for immediate execution", taskId, task.Name);

        // 唤醒对应的调度线程
        if (string.Equals(task.Source, EvolutionSource, StringComparison.Ordinal))
            _evolutionTriggerChannel.Writer.TryWrite(true);
        else
            _scheduledTriggerChannel.Writer.TryWrite(true);
    }

    /// <summary>
    /// 注册计划任务（如果同名活跃任务已存在则跳过）
    /// </summary>
    public async Task<string?> RegisterTaskAsync(ScheduledTask task, CancellationToken ct = default)
    {
        if (await _taskManager.HasActiveTaskAsync(task.Name, ct))
        {
            _logger.LogDebug("[Scheduler] Task '{Name}' already exists, skipping", task.Name);
            return null;
        }

        var id = await _taskManager.CreateTaskAsync(task, ct);
        _logger.LogInformation("[Scheduler] Registered: {Id} — {Name} ({Type})", id, task.Name, task.Type);
        return id;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Scheduler] ⏰ Scheduler service starting...");

        // 启动延迟
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        // 检查 LLM 供应商 — 周期性重试，避免永久阻塞
        var registry = _services.GetRequiredService<ProviderRegistry>();
        while (registry.GetAll().Count == 0 && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("[Scheduler] No LLM providers — retrying in 60s (请在设置中配置供应商后重启)");
            await PublishStatusAsync("待机 — 未配置 LLM 供应商", false, stoppingToken);
            // 等待 60 秒或手动触发后重新检查，避免永久阻塞
            await WaitForTriggerOrTimeoutAsync(_triggerChannel, TimeSpan.FromSeconds(60), stoppingToken);
        }
        if (stoppingToken.IsCancellationRequested) return;

        _logger.LogInformation("[Scheduler] ⏰ Scheduler service activated");
        await PublishStatusAsync("已激活", false, stoppingToken);

        // 注册内置计划任务（进化任务 + 维护任务）
        await RegisterBuiltInTasksAsync(stoppingToken);

        // 启动两个独立线程：进化任务线程 + 定时任务线程
        var evolutionLoop = Task.Factory.StartNew(
            () => RunEvolutionLoopAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var scheduledLoop = Task.Factory.StartNew(
            () => RunScheduledTaskLoopAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        _logger.LogInformation("[Scheduler] ⏰ Two worker threads started (evolution + scheduled)");

        // 等待两个循环都结束
        await Task.WhenAll(evolutionLoop, scheduledLoop);

        _logger.LogInformation("[Scheduler] ⏰ Scheduler service stopped");
    }

    /// <summary>
    /// 进化任务专用线程循环
    /// </summary>
    private async Task RunEvolutionLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Scheduler] 🧬 Evolution thread started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunTaskCycleAsync(EvolutionSource, isEvolution: true, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler-Evolution] Cycle error");
                await PublishChatMessageAsync("evolution", BackgroundTaskPhase.Failed,
                    $"进化调度异常：{Truncate(ex.Message, 80)}", ct);
                await PublishStatusAsync($"进化调度异常：{Truncate(ex.Message, 80)}", false, ct);
            }

            await WaitForTriggerOrTimeoutAsync(_evolutionTriggerChannel, EvolutionPollInterval, ct);
        }

        _logger.LogInformation("[Scheduler] 🧬 Evolution thread stopped");
    }

    /// <summary>
    /// 定时任务专用线程循环
    /// </summary>
    private async Task RunScheduledTaskLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Scheduler] 📋 Scheduled task thread started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunTaskCycleAsync(EvolutionSource, isEvolution: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Scheduler-Scheduled] Cycle error");
                await PublishChatMessageAsync("scheduler", BackgroundTaskPhase.Failed,
                    $"定时任务调度异常：{Truncate(ex.Message, 80)}", ct);
                await PublishStatusAsync($"定时任务调度异常：{Truncate(ex.Message, 80)}", false, ct);
            }

            await WaitForTriggerOrTimeoutAsync(_scheduledTriggerChannel, ScheduledPollInterval, ct);
        }

        _logger.LogInformation("[Scheduler] 📋 Scheduled task thread stopped");
    }

    /// <summary>
    /// 注册内置计划任务 — 定时任务线程和进化检查线程
    /// </summary>
    private async Task RegisterBuiltInTasksAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Scheduler] Registering built-in tasks...");

        // 1. 进化检查任务（兼容旧任务名）- 仅负责为缺口生成实现计划
        var evolutionTask = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = EvolutionPlanningTaskName,
            Description = "定期扫描待实现能力缺口，并为其生成树形实现计划",
            Type = ScheduledTaskType.Recurring,
            MessageTemplate = """
                你正在执行“进化规划”任务。你的职责只有一个：为缺失能力生成高质量实现计划，不要直接实施代码或创建技能。

                ## 目标
                - 扫描待解决的能力缺口
                - 找出“还没有实现计划”的缺口
                - 按麦肯锡 Issue Tree / MECE 方法，为该缺口创建树形实现计划
                - 本轮最多只为 1 个缺口创建计划
                - 不要执行计划步骤，不要做验收，不要 resolve_gap

                ## 执行步骤
                1. 使用 self_evolve list_gaps 查看待解决的能力缺口
                2. 如果没有待处理缺口，回复“系统能力完整，无需规划”并结束
                3. 优先处理 Priority 最大的缺口；若最高优先级缺口已有计划，则继续检查下一条
                4. 对候选缺口调用 self_evolve list_plan
                5. 如果该缺口还没有计划，则调用 self_evolve plan_gap 创建计划
                6. 如果所有缺口都已有计划，则回复“当前缺口均已有实现计划，无需重新规划”并结束

                ### MECE 分解原则（相互独立、完全穷尽）
                - 同级步骤之间不重叠（Mutually Exclusive）
                - 同级步骤合起来覆盖全部问题（Collectively Exhaustive）

                ### 三层结构
                - **诊断层 (diagnostic)** — Why: 为什么做不到？检查前置条件
                - **实现层 (implementation)** — How: 具体怎么做？编码/配置/安装
                - **验证层 (verification)** — Verify: 做完了吗？编译/测试/确认

                ### 每个步骤必须包含
                - hypothesis: 这一步的前提假设（例如："系统未安装 ffmpeg"）
                - acceptance_criteria: 验收标准（例如："ffmpeg -version 返回版本号"）
                - step_type: 步骤类型 (diagnostic / implementation / verification)

                ### 示例：视频编辑能力
                ```json
                [
                  {"title":"环境诊断","step_type":"diagnostic","hypothesis":"可能缺少视频处理工具链","acceptance_criteria":"明确当前环境的工具链状态","children":[
                    {"title":"检查 ffmpeg","step_type":"diagnostic","hypothesis":"系统可能未安装 ffmpeg","acceptance_criteria":"ffmpeg -version 返回版本号或明确未安装"},
                    {"title":"检查磁盘空间","step_type":"diagnostic","hypothesis":"视频处理需要足够磁盘空间","acceptance_criteria":"可用空间 > 1GB"}
                  ]},
                  {"title":"工具链搭建","step_type":"implementation","hypothesis":"诊断后可能需要安装工具","acceptance_criteria":"ffmpeg 命令可用","children":[
                    {"title":"安装 ffmpeg","step_type":"implementation","hypothesis":"ffmpeg 未安装","acceptance_criteria":"ffmpeg -version 成功"},
                    {"title":"配置 PATH","step_type":"implementation","hypothesis":"安装后可能未加入 PATH","acceptance_criteria":"任意目录下 ffmpeg 可用"}
                  ]},
                  {"title":"操作技能","step_type":"implementation","hypothesis":"需要封装视频操作为可调用技能","acceptance_criteria":"所有技能创建成功","children":[
                    {"title":"视频剪切 Skill","step_type":"implementation","hypothesis":"用户需要视频剪切能力","acceptance_criteria":"skill.json + SKILL.md 创建完成"},
                    {"title":"视频合并 Skill","step_type":"implementation","hypothesis":"用户需要视频合并能力","acceptance_criteria":"skill.json + SKILL.md 创建完成"}
                  ]},
                  {"title":"端到端验证","step_type":"verification","hypothesis":"所有组件就绪后需要集成验证","acceptance_criteria":"执行一次完整的视频剪切操作成功"}
                ]
                ```

                ## 工作区信息
                - 源码路径: 使用 system_info 或 search_files 查找 .sln 文件确定工作区
                - 工具目录: 通常在 src/OwnerAI.Agent.Tools/ 或 src/OwnerAI.Agent/

                注意：本任务只负责创建计划，不做实现、不做验收、不关闭缺口。
                """,
            Persona = "你是 OwnerAI 的自我进化模块。你的使命是不断增强系统能力 — 分析能力缺口，编写高质量代码，实现新工具，让 OwnerAI 变得更强大。你具有完整的文件读写、命令执行、网络搜索能力。你编写的代码必须遵循项目规范，通过编译验证。",
            Temperature = 0.3f,
            Priority = 5, // 最高优先级
            Interval = TimeSpan.FromMinutes(10),
            MaxRetries = 3,
            Source = EvolutionSource,
        };

        await _taskManager.EnsureBuiltInTaskAsync(evolutionTask, ct);
        _logger.LogInformation("[Scheduler] ✅ Registered evolution planning task (compat name: 进化检查)");

        var evolutionExecutionTask = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = EvolutionExecutionTaskName,
            Description = "按已有实现计划逐步执行能力实现步骤",
            Type = ScheduledTaskType.Recurring,
            MessageTemplate = """
                你正在执行“进化执行”任务。你的职责是：基于已有实现计划，逐步完成实现步骤；不要重新规划，也不要过早验收。

                ## 目标
                - 找到已有计划且仍有待执行实现步骤的能力缺口
                - 每轮仅执行 1-2 个步骤
                - 严格依据 hypothesis 与 acceptance_criteria 执行
                - 不要重新调用 plan_gap
                - 不要 resolve_gap，验收交给“进化验收”任务

                ## 执行步骤
                1. 使用 self_evolve list_plan 查看当前目标缺口的计划与进度
                2. 使用 self_evolve execute_step 获取下一个待执行步骤
                3. 严格按步骤要求执行，并在满足验收标准后调用 self_evolve complete_step
                4. 若当前步骤是假设不成立的诊断步骤，可 success=true 并说明“假设不成立，无需处理”
                5. 每轮只完成 1-2 步，然后结束

                ## 约束
                - 必须先编译/验证再标记实现步骤完成
                - 可在实现步骤中创建运行时 Skill，但不要关闭缺口
                - 若步骤全部完成，应结束并等待“进化验收”任务接管
                """,
            Persona = "你是 OwnerAI 的自我进化执行模块。你的职责是严格按实现计划逐步落地能力，谨慎修改代码或生成技能，并以验收标准为完成依据。",
            Temperature = 0.2f,
            Priority = 4,
            Interval = TimeSpan.FromMinutes(5),
            MaxRetries = 3,
            Source = EvolutionSource,
        };

        await _taskManager.EnsureBuiltInTaskAsync(evolutionExecutionTask, ct);
        _logger.LogInformation("[Scheduler] ✅ Registered evolution execution task (every 5 minutes)");

        var evolutionVerificationTask = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = EvolutionVerificationTaskName,
            Description = "对已完成实现计划的能力缺口做验收测试，必要时形成 Skill 并关闭缺口",
            Type = ScheduledTaskType.Recurring,
            MessageTemplate = """
                你正在执行“进化验收”任务。你的职责是：只处理已完成实施的能力缺口，做最终验收、形成 Skill（如适用），然后关闭缺口。

                ## 目标
                - 找到已经进入验收阶段的能力缺口
                - 执行编译、测试、端到端验证
                - 对 skill 类缺口，验证通过后形成 Skill
                - 验收通过后再调用 self_evolve resolve_gap

                ## 执行步骤
                1. 使用 self_evolve list_plan 查看当前目标缺口计划
                2. 根据 verification 步骤执行验证、编译、测试与端到端检查
                3. 若缺口属于 skill 类且已具备交付内容，调用 self_evolve create_skill 形成 Skill
                4. 仅在验收通过时调用 self_evolve resolve_gap
                5. 若验收失败，不要关闭缺口，明确失败原因并结束

                ## 约束
                - 只有真正通过验证的能力才允许关闭缺口
                - Skill 形成后再关闭缺口
                """,
            Persona = "你是 OwnerAI 的自我进化验收模块。你的职责是从结果导向出发，对已完成实施的能力做最终验证、交付和关闭，确保只有真正可用的能力才被标记为完成。",
            Temperature = 0.2f,
            Priority = 3,
            Interval = TimeSpan.FromMinutes(10),
            MaxRetries = 2,
            Source = EvolutionSource,
        };

        await _taskManager.EnsureBuiltInTaskAsync(evolutionVerificationTask, ct);
        _logger.LogInformation("[Scheduler] ✅ Registered evolution verification task (every 10 minutes)");

        // 4. 系统维护任务 - 每小时执行一次，清理旧日志、检查系统状态
        var maintenanceTask = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = "系统维护",
            Description = "定期清理旧日志、检查系统状态、优化性能",
            Type = ScheduledTaskType.Recurring,
            MessageTemplate = """
                你正在执行系统维护任务。请执行以下检查和维护操作:

                ## 系统检查
                1. 使用 system_info 检查系统资源 (CPU、内存、磁盘)
                2. 使用 process_list 查看是否有异常进程
                3. 使用 schedule_task status 检查调度器状态

                ## 日志清理
                1. 查找项目目录下的日志文件 (如 *.log)
                2. 删除超过 7 天的旧日志文件
                3. 报告清理结果

                ## 任务清理
                1. 使用 schedule_task history 查看执行历史
                2. 清理已完成超过 30 天的任务记录 (如有需要)

                ## 报告
                汇总本次维护的结果，包括:
                - 系统资源状态
                - 清理的日志文件数量和大小
                - 任何发现的问题或建议
                """,
            Persona = "你是 OwnerAI 的系统维护专家。你的职责是保持系统健康运行 — 清理垃圾文件、监控资源使用、发现潜在问题。你操作谨慎，只在确认安全的情况下删除文件。",
            Temperature = 0.2f,
            Priority = 3, // 中等优先级
            Interval = TimeSpan.FromHours(1),
            MaxRetries = 2,
            Source = "system",
        };

        await _taskManager.EnsureBuiltInTaskAsync(maintenanceTask, ct);
        _logger.LogInformation("[Scheduler] ✅ Registered maintenance task (every hour)");

        _logger.LogInformation("[Scheduler] Built-in tasks registration completed");
    }

    private static string GetTaskStageTag(ScheduledTask task)
    {
        if (task.Source != EvolutionSource)
            return "[📋 定时任务]";

        return task.Name switch
        {
            EvolutionPlanningTaskName => "[🧭 规划]",
            EvolutionExecutionTaskName => "[🛠 执行]",
            EvolutionVerificationTaskName => "[🧪 验收]",
            _ => "[🧬 进化]",
        };
    }

    private static ModelWorkCategory GetWorkCategoryForTask(ScheduledTask task)
    {
        if (task.Source != EvolutionSource)
            return ModelWorkCategory.DeepReasoning;

        return task.Name switch
        {
            EvolutionPlanningTaskName => ModelWorkCategory.EvolutionPlanning,
            EvolutionExecutionTaskName => ModelWorkCategory.EvolutionExecution,
            EvolutionVerificationTaskName => ModelWorkCategory.EvolutionVerification,
            _ => ModelWorkCategory.DeepReasoning,
        };
    }

    private static string FormatTaskChatMessage(ScheduledTask task, string message)
        => $"{GetTaskStageTag(task)} {message}";

    private async Task<EvolutionGap?> GetTargetGapForTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        if (task.Source != EvolutionSource)
            return null;

        var evolutionManager = _services.GetService<IEvolutionManager>();
        if (evolutionManager is null)
            return null;

        return task.Name switch
        {
            EvolutionPlanningTaskName => await evolutionManager.GetNextGapForPlanningAsync(ct),
            EvolutionExecutionTaskName => await evolutionManager.GetNextGapForImplementationAsync(ct),
            EvolutionVerificationTaskName => await evolutionManager.GetNextGapForVerificationAsync(ct),
            _ => null,
        };
    }

    /// <summary>
    /// 统一调度循环：检查就绪任务 → 获取锁 → 执行 → 更新状态 → 汇报结果
    /// </summary>
    /// <param name="sourceKey">进化任务来源标签 (用于区分进化/定时任务)</param>
    /// <param name="isEvolution">true = 仅拉取指定 source 的任务；false = 排除该 source</param>
    /// <param name="ct">取消令牌</param>
    private async Task RunTaskCycleAsync(string sourceKey, bool isEvolution, CancellationToken ct)
    {
        var threadLabel = isEvolution ? "Evolution" : "Scheduled";

        // 用户空闲检测 — 用户活跃时不执行后台任务
        if (!IsUserIdle())
        {
            _logger.LogInformation("[Scheduler-{Thread}] Skip cycle: user is active", threadLabel);
            return;
        }

        // 按来源拉取下一个就绪任务
        var task = isEvolution
            ? await _taskManager.GetNextReadyTaskBySourceAsync(sourceKey, ct)
            : await _taskManager.GetNextReadyTaskExcludingSourceAsync(sourceKey, ct);

        if (task is null)
        {
            _logger.LogDebug("[Scheduler-{Thread}] Skip cycle: no ready tasks", threadLabel);
            return;
        }

        var dispatchDetail = BuildTaskStateSummary(ScheduledTaskStatus.Dispatching, "调度器已选中，准备派发");
        if (!await _taskManager.TryMarkDispatchingAsync(task.Id, dispatchDetail, ct))
        {
            _logger.LogInformation("[Scheduler-{Thread}] Skip task {Id}: dispatch CAS rejected (likely already dispatching/running)",
                threadLabel, task.Id);
            return;
        }

        // 根据任务实际 Source 确定消息来源和标签（避免依赖 isEvolution 导致标签错误）
        var chatSource = task.Source == EvolutionSource ? "evolution" : "scheduler";
        var taskTypeLabel = task.Source == EvolutionSource ? "进化任务" : "定时任务";
        var targetGap = await GetTargetGapForTaskAsync(task, ct);

        _logger.LogInformation("[Scheduler-{Thread}] Executing: {Id} — {Name} (P{Priority})",
            threadLabel, task.Id, task.Name, task.Priority);

        await PublishStatusAsync(BuildPhaseText(ScheduledTaskStatus.Dispatching, task.Name, targetGap?.Description), false, ct, task.Id, task.Name);

        // ── 触发汇报 ──
        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Start,
            FormatTaskChatMessage(task, $"{taskTypeLabel}触发：{task.Name}"), ct, task.Id, task.Name, targetGap?.Id);

        await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Running, ct: ct);
        ChangeActiveTaskExecutions(1);

        _ = Task.Run(() => ExecuteTaskInBackgroundAsync(task, threadLabel, chatSource, taskTypeLabel, targetGap, ct), CancellationToken.None);
    }

    private async Task ExecuteTaskInBackgroundAsync(
        ScheduledTask task,
        string threadLabel,
        string chatSource,
        string taskTypeLabel,
        EvolutionGap? targetGap,
        CancellationToken ct)
    {
        var lockAcquired = false;

        try
        {
            // 获取 LLM 互斥锁 — 非阻塞尝试，等待最多 5 秒
            if (!await _llmMutex.WaitAsync(TimeSpan.FromSeconds(5), ct))
            {
                _logger.LogInformation("[Scheduler-{Thread}] Defer task {Id}: LLM busy", threadLabel, task.Id);
                var detail = BuildTaskStateSummary(ScheduledTaskStatus.WaitingForLlm, "LLM 忙碌中，延后执行");
                await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.WaitingForLlm,
                    lastResult: detail, nextRunAt: DateTimeOffset.Now.Add(DeferredRetryDelay), ct: ct);
                await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Progress,
                    FormatTaskChatMessage(task, detail), ct, task.Id, task.Name, targetGap?.Id);
                await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Completed,
                    FormatTaskChatMessage(task, $"{task.Name} 已重新排队，稍后重试"), ct, task.Id, task.Name, targetGap?.Id);
                return;
            }

            lockAcquired = true;

            // 再次检查用户空闲 — 获取锁期间用户可能恢复活跃
            if (!IsUserIdle())
            {
                _logger.LogInformation("[Scheduler-{Thread}] Defer task {Id}: user became active while waiting for LLM", threadLabel, task.Id);
                var detail = BuildTaskStateSummary(ScheduledTaskStatus.WaitingForLlm, "用户活跃，延后执行");
                await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.WaitingForLlm,
                    lastResult: detail, nextRunAt: DateTimeOffset.Now.Add(DeferredRetryDelay), ct: ct);
                await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Progress,
                    FormatTaskChatMessage(task, detail), ct, task.Id, task.Name, targetGap?.Id);
                await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Completed,
                    FormatTaskChatMessage(task, $"{task.Name} 已重新排队，等待用户空闲后继续"), ct, task.Id, task.Name, targetGap?.Id);
                return;
            }

            await PublishStatusAsync(BuildPhaseText(ScheduledTaskStatus.Running, task.Name, targetGap?.Description), true, ct, task.Id, task.Name);

            // ── 执行汇报 ──
            await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Progress,
                FormatTaskChatMessage(task, $"开始执行{taskTypeLabel}：{task.Name}"), ct, task.Id, task.Name, targetGap?.Id);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TaskTimeout);

            var result = await ExecuteTaskAgentAsync(task, chatSource, targetGap, cts.Token);

            await _taskManager.RecordExecutionAsync(new TaskExecutionRecord
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                TaskId = task.Id,
                TaskName = task.Name,
                Success = result.Outcome == TaskExecutionOutcome.Completed,
                Summary = result.Summary,
                PrimaryFailureSummary = result.PrimaryFailureSummary,
                ToolOverview = result.ToolOverview,
                FullLog = result.FullLog,
                ToolCallCount = result.ToolCallCount,
                Duration = result.Duration,
            }, ct);

            switch (result.Outcome)
            {
                case TaskExecutionOutcome.Completed:
                {
                    _logger.LogInformation("[Scheduler-{Thread}] ✅ Task {Id} completed: {Summary}",
                        threadLabel, task.Id, Truncate(result.Summary, 100));

                    if (task.Type == ScheduledTaskType.Recurring && task.Interval.HasValue)
                    {
                        var nextRun = DateTimeOffset.Now.Add(task.Interval.Value);
                        await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Pending,
                            lastResult: result.Summary, nextRunAt: nextRun, ct: ct);
                        await PublishStatusAsync($"✅ {Truncate(task.Name, 30)} → 下次：{nextRun:HH:mm}", false, ct, task.Id, task.Name);

                        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Completed,
                            FormatTaskChatMessage(task, $"{task.Name} 执行完成（耗时 {result.Duration.TotalSeconds:F1}s，工具调用 {result.ToolCallCount} 次）\n下次执行：{nextRun:yyyy-MM-dd HH:mm}\n摘要：{Truncate(result.Summary, 200)}"),
                            ct, task.Id, task.Name, targetGap?.Id);
                    }
                    else if (task.Type == ScheduledTaskType.Cron && task.CronExpression is not null)
                    {
                        var nextRun = CronHelper.GetNextOccurrence(task.CronExpression, DateTimeOffset.Now);
                        if (nextRun.HasValue)
                        {
                            await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Pending,
                                lastResult: result.Summary, nextRunAt: nextRun.Value, ct: ct);
                            await PublishStatusAsync($"✅ {Truncate(task.Name, 30)} → 下次：{nextRun.Value:MM-dd HH:mm}", false, ct, task.Id, task.Name);
                        }
                        else
                        {
                            await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Completed,
                                lastResult: result.Summary, ct: ct);
                        }

                        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Completed,
                            FormatTaskChatMessage(task, $"{task.Name} 执行完成（耗时 {result.Duration.TotalSeconds:F1}s，工具调用 {result.ToolCallCount} 次）\n摘要：{Truncate(result.Summary, 200)}"),
                            ct, task.Id, task.Name, targetGap?.Id);
                    }
                    else
                    {
                        await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Completed,
                            lastResult: result.Summary, ct: ct);
                        await PublishStatusAsync($"✅ 已完成：{Truncate(task.Name, 40)}", false, ct, task.Id, task.Name);

                        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Completed,
                            FormatTaskChatMessage(task, $"{task.Name} 已完成（耗时 {result.Duration.TotalSeconds:F1}s，工具调用 {result.ToolCallCount} 次）\n摘要：{Truncate(result.Summary, 200)}"),
                            ct, task.Id, task.Name, targetGap?.Id);
                    }

                    break;
                }

                case TaskExecutionOutcome.Incomplete:
                {
                    var nextRun = DateTimeOffset.Now.Add(IncompleteRetryDelay);
                    var detail = BuildTaskStateSummary(ScheduledTaskStatus.RetryWaiting, result.PrimaryFailureSummary ?? result.Summary);
                    await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.RetryWaiting,
                        lastResult: detail, nextRunAt: nextRun, ct: ct);
                    await PublishStatusAsync($"⏳ 继续：{Truncate(task.Name, 30)}", false, ct, task.Id, task.Name);

                    await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Completed,
                        FormatTaskChatMessage(task, $"{detail}\n下次执行：{nextRun:HH:mm}"),
                        ct, task.Id, task.Name, targetGap?.Id);
                    break;
                }

                default:
                {
                    _logger.LogWarning("[Scheduler-{Thread}] ❌ Task {Id} failed: {Log}",
                        threadLabel, task.Id, Truncate(result.Summary, 200));

                    var terminalFailureStatus = ShouldMarkTaskBlocked(result)
                        ? ScheduledTaskStatus.Blocked
                        : ScheduledTaskStatus.Failed;

                    if (task.ConsecutiveFailures + 1 >= task.MaxRetries)
                    {
                        var detail = BuildTaskStateSummary(terminalFailureStatus, result.PrimaryFailureSummary ?? result.Summary);
                        await _taskManager.UpdateTaskAsync(task.Id, terminalFailureStatus,
                            lastResult: detail, ct: ct);
                        await PublishStatusAsync(BuildPhaseText(terminalFailureStatus, task.Name, detail), false, ct, task.Id, task.Name);

                        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                            FormatTaskChatMessage(task, $"{detail}\n已达最大重试次数 {task.MaxRetries}"),
                            ct, task.Id, task.Name, targetGap?.Id);
                    }
                    else if (terminalFailureStatus == ScheduledTaskStatus.Blocked)
                    {
                        var detail = BuildTaskStateSummary(ScheduledTaskStatus.Blocked, result.PrimaryFailureSummary ?? result.Summary);
                        await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Blocked,
                            lastResult: detail, ct: ct);
                        await PublishStatusAsync(BuildPhaseText(ScheduledTaskStatus.Blocked, task.Name, detail), false, ct, task.Id, task.Name);

                        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                            FormatTaskChatMessage(task, detail),
                            ct, task.Id, task.Name, targetGap?.Id);
                    }
                    else if (task.Type == ScheduledTaskType.Recurring && task.Interval.HasValue)
                    {
                        var retryDelay = TimeSpan.FromMinutes(5);
                        var detail = BuildTaskStateSummary(ScheduledTaskStatus.RetryWaiting, result.PrimaryFailureSummary ?? result.Summary);
                        await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.RetryWaiting,
                            lastResult: detail, nextRunAt: DateTimeOffset.Now.Add(retryDelay), ct: ct);

                        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                            FormatTaskChatMessage(task, $"{detail}\n第 {task.ConsecutiveFailures + 1}/{task.MaxRetries} 次失败，5 分钟后重试"),
                            ct, task.Id, task.Name, targetGap?.Id);
                    }
                    else if (task.Type == ScheduledTaskType.Cron && task.CronExpression is not null)
                    {
                        var nextRun = CronHelper.GetNextOccurrence(task.CronExpression, DateTimeOffset.Now);
                        if (nextRun.HasValue)
                        {
                            var detail = BuildTaskStateSummary(ScheduledTaskStatus.RetryWaiting, result.PrimaryFailureSummary ?? result.Summary);
                            await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.RetryWaiting,
                                lastResult: detail, nextRunAt: nextRun.Value, ct: ct);

                            await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                                FormatTaskChatMessage(task, $"{detail}\n下次执行：{nextRun.Value:MM-dd HH:mm}"),
                                ct, task.Id, task.Name, targetGap?.Id);
                        }
                        else
                        {
                            var detail = BuildTaskStateSummary(ScheduledTaskStatus.Failed, result.PrimaryFailureSummary ?? result.Summary);
                            await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Failed,
                                lastResult: detail, ct: ct);

                            await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                                FormatTaskChatMessage(task, detail),
                                ct, task.Id, task.Name, targetGap?.Id);
                        }
                    }
                    else
                    {
                        var detail = BuildTaskStateSummary(ScheduledTaskStatus.Failed, result.PrimaryFailureSummary ?? result.Summary);
                        await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Failed,
                            lastResult: detail, ct: ct);
                        await PublishStatusAsync($"❌ 失败：{Truncate(task.Name, 40)}", false, ct, task.Id, task.Name);

                        await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                            FormatTaskChatMessage(task, detail),
                            ct, task.Id, task.Name, targetGap?.Id);
                    }

                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("[Scheduler-{Thread}] ⏰ Task {Id} timed out", threadLabel, task.Id);
            var detail = BuildTaskStateSummary(ScheduledTaskStatus.Failed, "执行超时");
            await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.Failed,
                lastResult: detail, ct: ct);
            await PublishStatusAsync($"⏰ 超时：{Truncate(task.Name, 40)}", false, ct, task.Id, task.Name);

            await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                FormatTaskChatMessage(task, detail), ct, task.Id, task.Name, targetGap?.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Scheduler-{Thread}] Background execution error for task {Id}", threadLabel, task.Id);
            var detail = BuildTaskStateSummary(ScheduledTaskStatus.RetryWaiting, ex.Message);
            await _taskManager.UpdateTaskAsync(task.Id, ScheduledTaskStatus.RetryWaiting,
                lastResult: detail, nextRunAt: DateTimeOffset.Now.Add(DeferredRetryDelay), ct: ct);
            await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Failed,
                FormatTaskChatMessage(task, detail), ct, task.Id, task.Name, targetGap?.Id);
        }
        finally
        {
            if (lockAcquired)
                _llmMutex.Release();

            ChangeActiveTaskExecutions(-1);
        }
    }

    private async Task<string> BuildTaskUserMessageAsync(ScheduledTask task, EvolutionGap? targetGap, CancellationToken ct)
    {
        if (task.Source != EvolutionSource)
            return task.MessageTemplate + GapReportingInstruction + TaskStatusInstruction;

        if (task.Name is not (EvolutionPlanningTaskName or EvolutionExecutionTaskName or EvolutionVerificationTaskName))
            return task.MessageTemplate + TaskStatusInstruction;

        var evolutionManager = _services.GetService<IEvolutionManager>();
        if (evolutionManager is null)
            return task.MessageTemplate;

        if (targetGap is null)
        {
            var noWorkText = task.Name switch
            {
                EvolutionPlanningTaskName => "当前没有待规划缺口，请回复“当前无待规划缺口”并结束，不要执行其他动作。",
                EvolutionExecutionTaskName => "当前没有待实施缺口，请回复“当前无待实施缺口”并结束，不要执行其他动作。",
                EvolutionVerificationTaskName => "当前没有待验收缺口，请回复“当前无待验收缺口”并结束，不要执行其他动作。",
                _ => "当前无任务。",
            };

            return $"{task.MessageTemplate}\n\n## 当前调度上下文（系统筛选）\n{noWorkText}\n{TaskStatusInstruction}";
        }

        var steps = await evolutionManager.GetPlanStepsAsync(targetGap.Id, ct);
        var completed = steps.Count(s => s.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped);
        var failed = steps.Count(s => s.Status == PlanStepStatus.Failed);
        var nextStep = await evolutionManager.GetNextPendingStepAsync(targetGap.Id, ct);

        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine(task.MessageTemplate);
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("## 当前调度上下文（系统已筛选本轮目标）");
        contextBuilder.AppendLine($"- 目标缺口 ID: {targetGap.Id}");
        contextBuilder.AppendLine($"- 缺口描述: {targetGap.Description}");
        contextBuilder.AppendLine($"- 优先级: P{targetGap.Priority}");
        contextBuilder.AppendLine($"- 类别: {targetGap.Category}");
        contextBuilder.AppendLine($"- 当前状态: {targetGap.Status}");
        contextBuilder.AppendLine($"- 来源: {targetGap.Source}");
        contextBuilder.AppendLine($"- 计划进度: {completed}/{steps.Count} 完成，失败 {failed}");
        if (nextStep is not null)
            contextBuilder.AppendLine($"- 当前下一步: [{nextStep.Id}] {nextStep.Title} ({nextStep.StepType})");

        contextBuilder.AppendLine();
        contextBuilder.AppendLine("请优先且只处理上述目标缺口，不要自行切换到其他缺口。");
        contextBuilder.AppendLine(TaskStatusInstruction);

        return contextBuilder.ToString();
    }

    /// <summary>
    /// 执行任务 — 创建 AgentContext 并运行 Agent ReAct 循环，过程中通过消息窗口汇报
    /// </summary>
    private async Task<TaskExecutionResult> ExecuteTaskAgentAsync(ScheduledTask task, string chatSource, EvolutionGap? targetGap, CancellationToken ct)
    {
        var agent = _services.GetRequiredService<IAgent>();
        var config = GetTaskConfig(task);

        var userMessage = await BuildTaskUserMessageAsync(task, targetGap, ct);

        var context = new AgentContext
        {
            SessionId = $"scheduler-{task.Id}-{DateTimeOffset.Now:yyyyMMddHHmm}",
            UserMessage = userMessage,
            Config = config,
            WorkCategory = GetWorkCategoryForTask(task),
            Role = GetAgentRoleForTask(task),
            DisabledTools = GetDisabledToolsForTask(task),
        };

        var responseBuilder = new StringBuilder();
        var fullLogBuilder = new StringBuilder();
        var toolCallCount = 0;
        var toolSuccessCount = 0;
        var toolFailureCount = 0;
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasPlanningPlanGapCall = false;
        var hasRetryableToolFailures = false;
        var hasNonRetryableToolFailures = false;
        ToolFailureCategory? primaryRetryableFailureCategory = null;
        ToolFailureCategory? primaryNonRetryableFailureCategory = null;
        string? primaryRetryableFailureSummary = null;
        string? primaryNonRetryableFailureSummary = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        fullLogBuilder.AppendLine($"═══ 任务执行日志 ═══");
        fullLogBuilder.AppendLine($"任务：{task.Name}");
        fullLogBuilder.AppendLine($"指令：{task.MessageTemplate}");
        fullLogBuilder.AppendLine($"开始：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        fullLogBuilder.AppendLine();

        await foreach (var chunk in agent.ExecuteAsync(context, ct))
        {
            if (chunk.ToolCall is not null)
            {
                toolCallCount++;
                toolNames.Add(chunk.ToolCall.ToolName);
                hasPlanningPlanGapCall |= IsPlanGapToolCall(chunk.ToolCall);
                if (!chunk.ToolCall.Success)
                {
                    toolFailureCount++;
                    var failureSummary = BuildToolFailureSummary(chunk.ToolCall);
                    if (chunk.ToolCall.Retryable)
                    {
                        hasRetryableToolFailures = true;
                        primaryRetryableFailureCategory ??= chunk.ToolCall.FailureCategory;
                        primaryRetryableFailureSummary ??= failureSummary;
                    }
                    else
                    {
                        hasNonRetryableToolFailures = true;
                        primaryNonRetryableFailureCategory ??= chunk.ToolCall.FailureCategory;
                        primaryNonRetryableFailureSummary ??= failureSummary;
                    }
                }
                else
                {
                    toolSuccessCount++;
                }

                var status = chunk.ToolCall.Success ? "✅" : "❌";
                fullLogBuilder.AppendLine($"🔧 [{chunk.ToolCall.ToolName}] {status} ({chunk.ToolCall.Duration.TotalSeconds:F1}s)");
                if (chunk.ToolCall.Parameters is not null)
                    fullLogBuilder.AppendLine($"   参数：{Truncate(chunk.ToolCall.Parameters, 200)}");
                if (chunk.ToolCall.Result is not null)
                    fullLogBuilder.AppendLine($"   结果：{Truncate(chunk.ToolCall.Result, 300)}");
                if (!string.IsNullOrWhiteSpace(chunk.ToolCall.ErrorCode))
                    fullLogBuilder.AppendLine($"   错误码：{chunk.ToolCall.ErrorCode}");
                if (!string.IsNullOrWhiteSpace(chunk.ToolCall.ErrorMessage))
                    fullLogBuilder.AppendLine($"   错误：{Truncate(chunk.ToolCall.ErrorMessage, 200)}");
                if (chunk.ToolCall.FailureCategory != ToolFailureCategory.Unknown)
                    fullLogBuilder.AppendLine($"   分类：{chunk.ToolCall.FailureCategory}");
                if (!string.IsNullOrWhiteSpace(chunk.ToolCall.SuggestedFix))
                    fullLogBuilder.AppendLine($"   建议：{Truncate(chunk.ToolCall.SuggestedFix, 200)}");
                if (!chunk.ToolCall.Success)
                    fullLogBuilder.AppendLine($"   可重试：{(chunk.ToolCall.Retryable ? "是" : "否")}");
                fullLogBuilder.AppendLine();

                // ── 工具调用过程汇报 ──
                var progressText = chunk.ToolCall.Success
                    ? $"[{task.Name}] 调用工具 [{chunk.ToolCall.ToolName}] {status} ({chunk.ToolCall.Duration.TotalSeconds:F1}s)"
                    : $"[{task.Name}] 调用工具 [{chunk.ToolCall.ToolName}] {status} ({chunk.ToolCall.Duration.TotalSeconds:F1}s)"
                      + (chunk.ToolCall.ErrorCode is { Length: > 0 } ? $" | {chunk.ToolCall.ErrorCode}" : string.Empty)
                      + (chunk.ToolCall.Retryable ? " | 可重试" : string.Empty);
                await PublishChatMessageAsync(chatSource, BackgroundTaskPhase.Progress,
                    FormatTaskChatMessage(task, progressText),
                    ct, task.Id, task.Name, targetGap?.Id);
            }

            if (chunk.Text is not null)
                responseBuilder.Append(chunk.Text);
        }

        sw.Stop();
        var response = responseBuilder.ToString();
        var outcome = ResolveTaskExecutionOutcome(response, hasRetryableToolFailures, hasNonRetryableToolFailures);
        var cleanedResponse = StripTaskStatusMarker(response).Trim();
        var primaryFailureCategory = primaryNonRetryableFailureCategory ?? primaryRetryableFailureCategory;
        var primaryFailureSummary = primaryNonRetryableFailureSummary ?? primaryRetryableFailureSummary;
        if (task.Name == EvolutionPlanningTaskName
            && !hasPlanningPlanGapCall
            && !HasExplicitNoPlanningConclusion(cleanedResponse))
        {
            outcome = TaskExecutionOutcome.Incomplete;
            primaryFailureCategory ??= ToolFailureCategory.ValidationError;
            primaryFailureSummary ??= "进化规划任务未调用 self_evolve plan_gap，且未明确输出“无需规划”";
            fullLogBuilder.AppendLine("═══ 规划约束校验 ═══");
            fullLogBuilder.AppendLine("未检测到 self_evolve plan_gap，且最终回复未明确说明“无需规划”，本轮按未完成处理。");
            fullLogBuilder.AppendLine();
        }

        var displaySummary = string.IsNullOrWhiteSpace(cleanedResponse) ? primaryFailureSummary ?? string.Empty : cleanedResponse;
        var toolOverview = BuildToolOverview(toolCallCount, toolSuccessCount, toolFailureCount, toolNames, primaryFailureSummary);

        fullLogBuilder.AppendLine("═══ 工具调用摘要 ═══");
        fullLogBuilder.AppendLine(toolOverview);
        fullLogBuilder.AppendLine();

        fullLogBuilder.AppendLine($"═══ AI 回复 ═══");
        fullLogBuilder.AppendLine(displaySummary);
        fullLogBuilder.AppendLine();
        fullLogBuilder.AppendLine($"═══ 执行完成 ═══");
        fullLogBuilder.AppendLine($"耗时：{sw.Elapsed.TotalSeconds:F1}s | 工具调用：{toolCallCount} 次 | 结果：{outcome}");

        return new TaskExecutionResult
        {
            Outcome = outcome,
            Summary = Truncate(displaySummary, 500),
            PrimaryFailureCategory = primaryFailureCategory,
            PrimaryFailureSummary = primaryFailureSummary,
            ToolOverview = toolOverview,
            FullLog = fullLogBuilder.ToString(),
            ToolCallCount = toolCallCount,
            Duration = sw.Elapsed,
        };
    }

    private static TaskExecutionOutcome ResolveTaskExecutionOutcome(
        string response,
        bool hasRetryableToolFailures,
        bool hasNonRetryableToolFailures)
    {
        if (response.Contains("[TASK_STATUS:FAILED]", StringComparison.OrdinalIgnoreCase))
            return TaskExecutionOutcome.Failed;

        if (response.Contains("[TASK_STATUS:INCOMPLETE]", StringComparison.OrdinalIgnoreCase))
            return TaskExecutionOutcome.Incomplete;

        if (response.Contains("[TASK_STATUS:COMPLETED]", StringComparison.OrdinalIgnoreCase))
            return TaskExecutionOutcome.Completed;

        if (hasNonRetryableToolFailures)
            return TaskExecutionOutcome.Failed;

        if (hasRetryableToolFailures)
            return TaskExecutionOutcome.Incomplete;

        return TaskExecutionOutcome.Completed;
    }

    private static string StripTaskStatusMarker(string response)
        => response
            .Replace("[TASK_STATUS:COMPLETED]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[TASK_STATUS:INCOMPLETE]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("[TASK_STATUS:FAILED]", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string BuildTaskStateSummary(ScheduledTaskStatus status, string? detail)
    {
        var prefix = status switch
        {
            ScheduledTaskStatus.Dispatching => "派发中：",
            ScheduledTaskStatus.Blocked => "阻塞原因：",
            ScheduledTaskStatus.WaitingForLlm => "等待 LLM：",
            ScheduledTaskStatus.RetryWaiting => "等待重试：",
            ScheduledTaskStatus.Failed => "失败原因：",
            ScheduledTaskStatus.Completed => "完成摘要：",
            ScheduledTaskStatus.Running => "运行中：",
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(detail))
            return prefix.TrimEnd('：');

        return string.Concat(prefix, Truncate(detail, 240));
    }

    private static string BuildToolFailureSummary(ToolCallInfo toolCall)
    {
        var category = toolCall.FailureCategory == ToolFailureCategory.Unknown
            ? string.Empty
            : $"{toolCall.FailureCategory} / ";
        var code = string.IsNullOrWhiteSpace(toolCall.ErrorCode) ? "unknown_error" : toolCall.ErrorCode;
        var retryable = toolCall.Retryable ? "（可重试）" : string.Empty;
        return $"{toolCall.ToolName} / {category}{code}{retryable}";
    }

    private static string BuildToolOverview(
        int toolCallCount,
        int toolSuccessCount,
        int toolFailureCount,
        IEnumerable<string> toolNames,
        string? primaryFailureSummary)
    {
        var toolNameList = toolNames.Take(6).ToList();
        var names = string.Join(", ", toolNameList);
        var suffix = toolCallCount > toolNameList.Count ? " 等更多工具/调用" : string.Empty;
        var summary = $"工具调用 {toolCallCount} 次 | 成功 {toolSuccessCount} | 失败 {toolFailureCount}";

        if (!string.IsNullOrWhiteSpace(names))
            summary += $" | 涉及工具：{names}{suffix}";

        if (!string.IsNullOrWhiteSpace(primaryFailureSummary))
            summary += $" | 主失败原因：{primaryFailureSummary}";

        return summary;
    }

    private static bool IsPlanGapToolCall(ToolCallInfo toolCall)
    {
        if (!toolCall.ToolName.Equals("self_evolve", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(toolCall.Parameters))
            return false;

        try
        {
            using var document = JsonDocument.Parse(toolCall.Parameters);
            if (document.RootElement.TryGetProperty("action", out var actionElement))
                return string.Equals(actionElement.GetString(), "plan_gap", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return toolCall.Parameters.Contains("\"action\":\"plan_gap\"", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool HasExplicitNoPlanningConclusion(string response)
        => response.Contains("无需规划", StringComparison.Ordinal)
            || response.Contains("当前缺口均已有实现计划", StringComparison.Ordinal)
            || response.Contains("系统能力完整", StringComparison.Ordinal);

    private static string AppendPrimaryFailure(string message, string? primaryFailureSummary)
        => string.IsNullOrWhiteSpace(primaryFailureSummary)
            ? message
            : $"{message}\n主失败原因：{primaryFailureSummary}";

    private static string BuildPhaseText(ScheduledTaskStatus status, string taskName, string? detail = null)
    {
        var prefix = status switch
        {
            ScheduledTaskStatus.Dispatching => "派发中：",
            ScheduledTaskStatus.Running => "运行中：",
            ScheduledTaskStatus.Blocked => "阻塞原因：",
            ScheduledTaskStatus.WaitingForLlm => "等待 LLM：",
            ScheduledTaskStatus.RetryWaiting => "等待重试：",
            ScheduledTaskStatus.Failed => "失败原因：",
            ScheduledTaskStatus.Completed => "完成摘要：",
            _ => string.Empty,
        };

        var name = Truncate(taskName, 24);
        if (string.IsNullOrWhiteSpace(detail))
            return string.Concat(prefix, name);

        return $"{prefix}{name} · {Truncate(StripStateSummaryPrefix(detail), 60)}";
    }

    private static string BuildTargetSummary(EvolutionGap? targetGap)
        => targetGap is null || string.IsNullOrWhiteSpace(targetGap.Description)
            ? string.Empty
            : $"目标缺口：{Truncate(targetGap.Description, 60)}";

    private static string StripStateSummaryPrefix(string detail)
    {
        ReadOnlySpan<string> prefixes = ["派发中：", "等待 LLM：", "等待重试：", "失败原因：", "阻塞原因：", "完成摘要：", "运行中："];
        foreach (var prefix in prefixes)
        {
            if (detail.StartsWith(prefix, StringComparison.Ordinal))
                return detail[prefix.Length..];
        }

        return detail;
    }

    private void ChangeActiveTaskExecutions(int delta)
    {
        var count = Interlocked.Add(ref _activeTaskExecutions, delta);
        IsRunningTask = count > 0;
    }

    /// <summary>
    /// 为任务构建 AgentConfig — 优先使用任务自身的 Persona/Temperature，否则使用默认配置
    /// </summary>
    private AgentConfig GetTaskConfig(ScheduledTask task)
    {
        try
        {
            var options = _services.GetRequiredService<IOptions<OwnerAIConfig>>();
            var baseConfig = options.Value.Agent;

            return baseConfig with
            {
                Persona = task.Persona ?? baseConfig.Persona,
                Temperature = task.Temperature ?? 0.3f,
            };
        }
        catch
        {
            return new AgentConfig
            {
                Persona = task.Persona ?? "你是 OwnerAI 的后台任务执行模块。",
                Temperature = task.Temperature ?? 0.3f,
                MaxToolIterations = 15,
            };
        }
    }

    private bool IsUserIdle()
        => DateTimeOffset.Now - _lastUserActivity > UserIdleThreshold;

    private static AgentRole GetAgentRoleForTask(ScheduledTask task)
        => string.Equals(task.Source, EvolutionSource, StringComparison.Ordinal)
            ? AgentRole.Evolution
            : AgentRole.Chat;

    private static HashSet<string> GetDisabledToolsForTask(ScheduledTask task)
    {
        if (task.Name == EvolutionPlanningTaskName)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "web_search",
                "web_fetch",
            };
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> WaitForTriggerOrTimeoutAsync(Channel<bool> channel, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await channel.Reader.ReadAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PublishStatusAsync(string phase, bool isActive, CancellationToken ct,
        string? taskId = null, string? taskName = null)
    {
        CurrentPhase = phase;
        IsRunningTask = isActive;

        try
        {
            var eventBus = _services.GetService<IEventBus>();
            if (eventBus is not null)
            {
                var stats = await _taskManager.GetStatsAsync(ct);
                await eventBus.PublishAsync(new SchedulerStatusEvent
                {
                    Phase = phase,
                    IsActive = isActive,
                    TaskId = taskId,
                    TaskName = taskName,
                    Stats = stats,
                }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Scheduler] Failed to publish status (non-critical)");
        }
    }

    private async Task PublishChatMessageAsync(string source, BackgroundTaskPhase phase, string message,
        CancellationToken ct, string? taskId = null, string? taskName = null, string? gapId = null)
    {
        try
        {
            var eventBus = _services.GetService<IEventBus>();
            if (eventBus is not null)
            {
                await eventBus.PublishAsync(new BackgroundTaskChatEvent
                {
                    Source = source,
                    Phase = phase,
                    Message = message,
                    TaskId = taskId,
                    TaskName = taskName,
                    GapId = gapId,
                }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Scheduler] Failed to publish chat message (non-critical)");
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "...");

    private sealed record TaskExecutionResult
    {
        public TaskExecutionOutcome Outcome { get; init; }
        public string Summary { get; init; } = "";
        public ToolFailureCategory? PrimaryFailureCategory { get; init; }
        public string? PrimaryFailureSummary { get; init; }
        public string? ToolOverview { get; init; }
        public string FullLog { get; init; } = "";
        public int ToolCallCount { get; init; }
        public TimeSpan Duration { get; init; }
    }

    private static bool ShouldMarkTaskBlocked(TaskExecutionResult result)
        => result.PrimaryFailureCategory is ToolFailureCategory.EnvironmentError or ToolFailureCategory.PermissionDenied;

    private enum TaskExecutionOutcome
    {
        Completed,
        Incomplete,
        Failed,
    }

    /// <summary>
    /// LLM 锁句柄 — 仅用户对话场景在释放时更新用户活跃时间
    /// </summary>
    private sealed class LlmLockHandle(SemaphoreSlim semaphore, SchedulerService scheduler, bool markUserActive) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (markUserActive)
                    scheduler._lastUserActivity = DateTimeOffset.Now;

                semaphore.Release();
            }
        }
    }
}
