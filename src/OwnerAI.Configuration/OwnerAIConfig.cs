namespace OwnerAI.Configuration;

/// <summary>
/// OwnerAI 根配置
/// </summary>
public sealed record OwnerAIConfig
{
    public const string SectionName = "OwnerAI";

    public AgentConfig Agent { get; init; } = new();
    public AgentRoleConfig ChatAgent { get; init; } = new();
    public AgentRoleConfig EvolutionAgent { get; init; } = new()
    {
        DisplayName = "EvolutionAgent",
        DefaultModel = "deepseek-r1:14B",
        FallbackModel = "gpt-4o",
        Temperature = 0.3f,
        MaxToolIterations = 30,
        Persona = "你是 OwnerAI 的自我进化模块。你的使命是不断增强系统能力 — 分析能力缺口，编写高质量代码，实现新工具，让 OwnerAI 变得更强大。你具有完整的文件读写、命令执行能力。你编写的代码必须遵循项目规范，通过编译验证。",
    };
    public SecurityConfig Security { get; init; } = new();
    public MemoryConfig Memory { get; init; } = new();
    public Mem0Config Mem0 { get; init; } = new();
    public PluginsConfig Plugins { get; init; } = new();
    public UIConfig UI { get; init; } = new();
}

/// <summary>
/// 角色级 Agent 配置覆盖
/// </summary>
public sealed record AgentRoleConfig
{
    public string DisplayName { get; init; } = "ChatAgent";
    public string? DefaultModel { get; init; }
    public string? FallbackModel { get; init; }
    public float? Temperature { get; init; }
    public int? MaxToolIterations { get; init; }
    public int? ContextWindowTokenBudget { get; init; }
    public string? Persona { get; init; }
}

/// <summary>
/// Agent 配置
/// </summary>
public sealed record AgentConfig
{
    public string DefaultModel { get; init; } = "gpt-4o";
    public string? FallbackModel { get; init; } = "deepseek-chat";
    public float Temperature { get; init; } = 0.7f;
    public int MaxToolIterations { get; init; } = 15;
    public int ContextWindowTokenBudget { get; init; } = 128_000;
    public string Persona { get; init; } = "你是一个高效、专业的个人 AI 助手。";
    public string Locale { get; init; } = "zh-CN";
    public Dictionary<string, ModelProviderConfig> Providers { get; init; } = [];
    public Dictionary<ModelWorkCategory, string> WorkModelAssignments { get; init; } = [];
}

/// <summary>
/// 模型供应商配置
/// </summary>
public sealed record ModelProviderConfig
{
    public required string Type { get; init; }
    public required string Endpoint { get; init; }
    public string? ApiKeySecret { get; init; }
    public string? ModelId { get; init; }
    public int Priority { get; init; }
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// 模型类别列表 — 一个模型可属于多个类别（如 GPT-4o 同时是 LLM + Vision + Coding）
    /// </summary>
    public ModelCategory[] Categories { get; init; } = [ModelCategory.LLM];

    /// <summary>
    /// 模型角色 — 主模型或次级模型
    /// </summary>
    public ModelRole Role { get; init; } = ModelRole.Primary;
}

/// <summary>
/// 模型类别 — 不同类别的模型擅长不同任务
/// </summary>
public enum ModelCategory
{
    /// <summary>通用对话 — 文本理解、推理、对话</summary>
    LLM,

    /// <summary>视觉理解/图像识别 — 图像理解、OCR、视觉问答、图像分类与检测</summary>
    Vision,

    /// <summary>多模态 — 同时处理文本、图像、音频、视频</summary>
    Multimodal,

    /// <summary>基础科学 — 数学、物理、化学、生物等学科推理</summary>
    Science,

    /// <summary>代码专用 — 代码生成、代码审查、调试</summary>
    Coding,

    /// <summary>深度推理 — 复杂逻辑、数学证明、多步推理（如 o1/o3/R1）</summary>
    Reasoning,

    /// <summary>文生图 — 从文字描述生成图片（AI 绘画、图像创作）</summary>
    ImageGen,

    /// <summary>语音 — 语音识别(STT)、语音合成(TTS)</summary>
    Audio,

    /// <summary>翻译 — 多语言专业翻译</summary>
    Translation,

    /// <summary>写作/创意 — 文案、小说、创意写作</summary>
    Writing,

    /// <summary>数据分析 — 数据处理、表格分析、统计</summary>
    DataAnalysis,

    /// <summary>嵌入向量 — 文本嵌入、语义检索</summary>
    Embedding,

    /// <summary>图生视频 — 从图片生成视频</summary>
    ImageToVideo,

    /// <summary>文生视频 — 从文字描述生成视频</summary>
    TextToVideo,
}

/// <summary>
/// 模型角色
/// </summary>
public enum ModelRole
{
    /// <summary>主模型 — 负责对话和任务分发</summary>
    Primary,

    /// <summary>次级模型 — 接受主模型分发的专项任务</summary>
    Secondary,
}

/// <summary>
/// 工作分类槽位 — 将系统中的实际工作流映射到具体模型
/// </summary>
public enum ModelWorkCategory
{
    ChatDefault,
    ChatFast,
    CodeTask,
    DeepReasoning,
    EvolutionPlanning,
    EvolutionExecution,
    EvolutionVerification,
    VisionAssist,
}

/// <summary>
/// 安全配置
/// </summary>
public sealed record SecurityConfig
{
    public ApprovalPolicy DefaultApprovalPolicy { get; init; } = ApprovalPolicy.HighRiskOnly;
    public SandboxMode Sandbox { get; init; } = SandboxMode.Enabled;
    public IReadOnlyList<string> AllowedSenders { get; init; } = [];
    public int AuditRetentionDays { get; init; } = 90;
}

/// <summary>
/// 审批策略
/// </summary>
public enum ApprovalPolicy
{
    AlwaysApprove,
    HighRiskOnly,
    AutoApprove,
}

/// <summary>
/// 沙箱模式
/// </summary>
public enum SandboxMode
{
    Disabled,
    Enabled,
    Strict,
}

/// <summary>
/// 记忆配置
/// </summary>
public sealed record MemoryConfig
{
    public string EmbeddingProvider { get; init; } = "onnx-local";
    public string OnnxModelPath { get; init; } = "models/all-MiniLM-L6-v2.onnx";
    public int EmbeddingDimension { get; init; } = 384;
    public int FragmentWindowSize { get; init; } = 2;
    public float FragmentMergeWeight { get; init; } = 0.7f;
    public int FragmentHistoryContext { get; init; } = 3;
    public string ConsolidationModel { get; init; } = "default";
    public int DefaultTopK { get; init; } = 5;
    public float VectorWeight { get; init; } = 0.7f;
    public float KeywordWeight { get; init; } = 0.2f;
    public float TemporalDecayWeight { get; init; } = 0.1f;
    public string DatabasePath { get; init; } = "data/memory.db";
    public int MaxFragments { get; init; } = 10_000;
    public int EmbeddingCacheSize { get; init; } = 5_000;
}

/// <summary>
/// 插件配置
/// </summary>
public sealed record PluginsConfig
{
    public string PluginsDirectory { get; init; } = "plugins";
    public bool AutoLoadPlugins { get; init; } = true;
    public IReadOnlyList<string> DisabledPlugins { get; init; } = [];
}

/// <summary>
/// UI 配置
/// </summary>
public sealed record UIConfig
{
    public string Theme { get; init; } = "system";
    public string HotKey { get; init; } = "Alt+Space";
    public bool MinimizeToTray { get; init; } = true;
    public bool ShowToolCalls { get; init; } = true;
}

/// <summary>
/// Mem0 向量记忆数据库配置
/// </summary>
public sealed record Mem0Config
{
    /// <summary>Mem0 安装根目录</summary>
    public string InstallPath { get; init; } = @"D:\AIMem";

    /// <summary>Mem0 REST 服务端口</summary>
    public int Port { get; init; } = 8019;

    /// <summary>Mem0 REST 服务主机</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>是否在启动时自动安装/启动 Mem0 服务</summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>服务启动超时秒数</summary>
    public int StartupTimeoutSeconds { get; init; } = 120;

    /// <summary>健康检查重试间隔毫秒</summary>
    public int HealthCheckIntervalMs { get; init; } = 1000;

    /// <summary>Mem0 服务的完整 BaseUrl</summary>
    public string BaseUrl => $"http://{Host}:{Port}";
}
