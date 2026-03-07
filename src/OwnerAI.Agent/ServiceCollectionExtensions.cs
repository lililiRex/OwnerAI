using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OwnerAI.Agent.Evolution;
using OwnerAI.Agent.Hooks;
using OwnerAI.Agent.Memory;
using OwnerAI.Agent.Orchestration;
using OwnerAI.Agent.Planner;
using OwnerAI.Agent.Plugins;
using OwnerAI.Agent.Providers;
using OwnerAI.Agent.Scheduler;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent;

/// <summary>
/// Agent 服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOwnerAIAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 供应商注册表
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<IModelMetricsManager, SqliteModelMetricsManager>();
        services.AddSingleton<ProviderFailover>();
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<ProviderFailover>());

        // 记忆管理器 — SQLite 持久化长期记忆
        services.AddSingleton<IMemoryManager, SqliteMemoryManager>();

        // 任务缓存配置 — 复用 Mem0Config 中的 InstallPath 作为数据存储目录
        services.AddSingleton<IOptions<Mem0Config>>(sp =>
        {
            var section = configuration.GetSection("OwnerAI:Mem0");
            var config = new Mem0Config();
            section.Bind(config);
            return Options.Create(config);
        });

        // 任务缓存管理器 — 内存热缓存 + SQLite 持久化存储
        services.AddSingleton<InMemoryTaskCacheManager>();
        services.AddSingleton<ITaskCacheManager, SqliteTaskCacheManager>();

        // 规划器
        services.AddSingleton<IPlanner, ReActPlanner>();

        // 工具编排器
        services.AddSingleton<ToolOrchestrator>();

        // 钩子管理器 — 收集 IHook 实现，按优先级分发事件
        services.AddSingleton<HookManager>();

        // 插件加载器 — 管理 IPlugin 生命周期
        services.AddHostedService<PluginLoader>();

        // 多模型协作工具 — 让主模型能分发任务给次级模型
        services.AddSingleton<IOwnerAITool, ModelRouterTool>();

        // 自我进化 — 能力缺口跟踪 + 自主改进
        services.AddSingleton<IEvolutionManager, SqliteEvolutionManager>();
        services.AddSingleton<IOwnerAITool, SelfEvolveTool>();

        // OpenClaw 外部技能桥接 — 让 Agent 能读取/执行 OpenClaw 格式的技能
        // TryAdd: 如果宿主已注册更完整的 IOpenClawSkillProvider（如 Desktop 的 OpenClawSkillScanner），则使用宿主的
        services.TryAddSingleton<IOpenClawSkillProvider, DefaultOpenClawSkillProvider>();
        services.AddSingleton<IOwnerAITool, OpenClawSkillTool>();

        // 技能开关管理 — TryAdd: 如果宿主已注册（如 Desktop 的 SkillStateManager），则使用宿主的
        services.TryAddSingleton<ISkillStateManager, NullSkillStateManager>();

        // 技能生命周期自动化 — 定期检查 trial 技能，自动晋升/淘汰
        services.AddHostedService<SkillLifecycleService>();

        // 统一调度器 — 管理所有后台任务（含进化），LLM 互斥锁保证并发安全
        services.AddSingleton<IScheduledTaskManager, SqliteScheduledTaskManager>();
        services.AddSingleton<SchedulerService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
        services.AddSingleton<IOwnerAITool, ScheduledTaskTool>();

        // 注: 进化任务由 SchedulerService 统一调度，不再单独注册 EvolutionBackgroundService
        // 保留类型注册以便外部查询进化状态（但不作为 HostedService 启动）
        services.AddSingleton<EvolutionBackgroundService>();

        // Agent 执行器
        services.AddTransient<IAgent, AgentExecutor>();

        return services;
    }
}
