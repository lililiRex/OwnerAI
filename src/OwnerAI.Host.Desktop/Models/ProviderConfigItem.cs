using CommunityToolkit.Mvvm.ComponentModel;

namespace OwnerAI.Host.Desktop.Models;

/// <summary>
/// 供应商预设模板
/// </summary>
public sealed record ProviderPreset(
    string Type,
    string DisplayName,
    string Endpoint,
    string DefaultModel,
    string ApiKeyPlaceholder);

/// <summary>
/// 供应商配置 UI 模型
/// </summary>
public sealed partial class ProviderConfigItem : ObservableObject
{
    /// <summary>
    /// 所有支持的供应商预设（选择类型后自动填充端点和模型）
    /// </summary>
    public static IReadOnlyList<ProviderPreset> Presets { get; } =
    [
        new("OpenAI",            "OpenAI",                "https://api.openai.com/v1",                          "gpt-4o",                   "sk-proj-..."),
        new("DeepSeek",          "DeepSeek",              "https://api.deepseek.com/v1",                        "deepseek-chat",            "sk-..."),
        new("Ollama",            "Ollama (本地)",          "http://localhost:11434/v1",                           "qwen2.5:7b",              "ollama"),
        new("Azure OpenAI",      "Azure OpenAI",          "https://{resource}.openai.azure.com/openai/deployments/{deployment}", "gpt-4o", "Azure API Key"),
        new("阿里云百炼",         "阿里云百炼",             "https://dashscope.aliyuncs.com/compatible-mode/v1",  "qwen-plus",                "sk-..."),
        new("阿里云百炼-Coding",  "阿里云百炼 (Coding)",    "https://dashscope.aliyuncs.com/compatible-mode/v1",  "qwen-coder-plus",          "sk-... (Coding Plan)"),
        new("阿里云百炼-视频",     "阿里云百炼 (视频生成)",   "https://dashscope.aliyuncs.com/compatible-mode/v1",  "wan2.6-i2v",               "sk-..."),
        new("火山引擎",           "火山引擎 (豆包)",        "https://ark.cn-beijing.volces.com/api/v3",           "doubao-1.5-pro-256k",      "接入点 API Key"),
        new("火山引擎-Coding",    "火山引擎 (Coding)",      "https://ark.cn-beijing.volces.com/api/v3",           "doubao-coder-1.5-pro",     "接入点 API Key (Coding)"),
    ];

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProviderType { get; set; } = "OpenAI";

    [ObservableProperty]
    public partial string Endpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Priority { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>
    /// 模型类别（逗号分隔，支持多选）: "LLM,Vision,Coding"
    /// </summary>
    [ObservableProperty]
    public partial string Categories { get; set; } = "LLM";

    partial void OnCategoriesChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) Categories = "LLM";
        OnPropertyChanged(nameof(CategoriesDisplay));
        OnPropertyChanged(nameof(SelectedCategoryItems));
    }

    /// <summary>显示用 — 多类别标签文字</summary>
    public string CategoriesDisplay
    {
        get
        {
            var cats = ParseCategories();
            var labels = cats.Select(c => CategoryDisplayItems.FirstOrDefault(d => d.Value == c)?.Display ?? c);
            return string.Join("  ", labels);
        }
    }

    /// <summary>解析当前类别为字符串列表</summary>
    public List<string> ParseCategories()
        => (Categories ?? "LLM").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>切换某个类别的选中状态</summary>
    public void ToggleCategory(string category)
    {
        var cats = ParseCategories();
        if (!cats.Remove(category))
        {
            cats.Add(category);
        }
        else if (cats.Count == 0)
        {
            cats.Add("LLM"); // 至少保留一个
        }
        Categories = string.Join(",", cats);
    }

    /// <summary>检查某个类别是否已选中</summary>
    public bool HasCategory(string category) => ParseCategories().Contains(category);

    /// <summary>选中的类别项列表 — 用于 UI 绑定</summary>
    public IReadOnlyList<DisplayItem> SelectedCategoryItems
        => CategoryDisplayItems.Where(d => HasCategory(d.Value)).ToList();

    /// <summary>
    /// 模型角色: Primary (主模型), Secondary (次级模型)
    /// </summary>
    [ObservableProperty]
    public partial string Role { get; set; } = "Primary";

    /// <summary>
    /// 模型是否支持 function calling (工具调用)
    /// 不支持工具的模型（如 DeepSeek R1）将以纯对话模式运行
    /// </summary>
    [ObservableProperty]
    public partial bool SupportsTools { get; set; } = true;

    private bool _isConnectionTestRunning;
    public bool IsConnectionTestRunning
    {
        get => _isConnectionTestRunning;
        set => SetProperty(ref _isConnectionTestRunning, value);
    }

    private string _connectionTestStatus = string.Empty;
    public string ConnectionTestStatus
    {
        get => _connectionTestStatus;
        set => SetProperty(ref _connectionTestStatus, value);
    }

    partial void OnRoleChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) Role = "Primary";
        OnPropertyChanged(nameof(SelectedRoleItem));
    }

    /// <summary>
    /// ComboBox SelectedItem 绑定 — 解决 ItemsRepeater 中 SelectedValue+SelectedValuePath 仅首项生效的 WinUI 问题
    /// </summary>
    public ProviderPreset? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Type == ProviderType);
        set { if (value is not null) ProviderType = value.Type; }
    }

    public DisplayItem? SelectedRoleItem
    {
        get => RoleDisplayItems.FirstOrDefault(d => d.Value == Role);
        set { if (value is not null) Role = value.Value; }
    }

    public string RegistryName => string.IsNullOrWhiteSpace(ModelId)
        ? Name
        : $"[{ModelId}|{ProviderType}]";

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(RegistryName));

    partial void OnModelIdChanged(string value) => OnPropertyChanged(nameof(RegistryName));

    partial void OnProviderTypeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(RegistryName));
    }

    /// <summary>
    /// 所有可用的模型类别值列表
    /// </summary>
    public static IReadOnlyList<string> AllCategoryValues { get; } =
        ["LLM", "Coding", "Reasoning", "Vision", "ImageGen", "ImageToVideo", "TextToVideo", "Audio", "Translation", "Writing", "DataAnalysis", "Embedding", "Science", "Multimodal"];

    /// <summary>
    /// 可选的模型角色列表
    /// </summary>
    public static IReadOnlyList<string> Roles { get; } =
        ["Primary", "Secondary"];

    /// <summary>
    /// ComboBox 用 — 模型类别（中文显示 + 英文值）
    /// </summary>
    public static IReadOnlyList<DisplayItem> CategoryDisplayItems { get; } =
    [
        new("🗣️ 通用对话 (LLM)", "LLM"),
        new("💻 代码专用", "Coding"),
        new("🧠 深度推理", "Reasoning"),
        new("👁️ 视觉理解/图像识别", "Vision"),
        new("🎨 文生图", "ImageGen"),
        new("🎬 图生视频", "ImageToVideo"),
        new("📹 文生视频", "TextToVideo"),
        new("🔊 语音", "Audio"),
        new("🌐 翻译", "Translation"),
        new("✍️ 写作/创意", "Writing"),
        new("📊 数据分析", "DataAnalysis"),
        new("📐 嵌入向量", "Embedding"),
        new("🔬 基础科学", "Science"),
        new("🎭 多模态", "Multimodal"),
    ];

    /// <summary>
    /// ComboBox 用 — 模型角色（中文显示 + 英文值）
    /// </summary>
    public static IReadOnlyList<DisplayItem> RoleDisplayItems { get; } =
    [
        new("⭐ 主模型", "Primary"),
        new("📎 次级模型", "Secondary"),
    ];

    /// <summary>
    /// 当前预设的 API Key 占位提示
    /// </summary>
    [ObservableProperty]
    public partial string ApiKeyPlaceholder { get; set; } = "sk-...";

    /// <summary>
    /// 切换供应商类型时自动填充端点、模型、名称（仅在编辑模式下）
    /// </summary>
    public void ApplyPreset(string type)
    {
        var preset = Presets.FirstOrDefault(p => p.Type == type);
        if (preset is null) return;

        ProviderType = preset.Type;
        Name = preset.DisplayName;
        Endpoint = preset.Endpoint;
        ModelId = preset.DefaultModel;
        ApiKeyPlaceholder = preset.ApiKeyPlaceholder;
    }

    /// <summary>
    /// 用户手动修改供应商类型时调用 — 标记为编辑模式并填充预设
    /// </summary>
    public void SwitchPreset(string type)
    {
        IsEditing = true;
        ApplyPreset(type);
    }

    /// <summary>
    /// 根据模型 ID 自动识别类别 — 内置知名模型能力数据库
    /// </summary>
    public static string AutoDetectCategories(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return "LLM";

        var id = modelId.ToLowerInvariant();

        // 按模型系列匹配
        foreach (var (pattern, categories) in s_modelCapabilities)
        {
            if (id.Contains(pattern))
                return categories;
        }

        return "LLM";
    }

    /// <summary>
    /// 知名模型能力数据库 — 模型名称模式 → 类别列表
    /// 顺序重要：更具体的模式应在前面
    /// </summary>
    private static readonly (string Pattern, string Categories)[] s_modelCapabilities =
    [
        // OpenAI
        ("gpt-4o", "LLM,Vision,Coding,Reasoning"),
        ("gpt-4-turbo", "LLM,Vision,Coding"),
        ("gpt-4-vision", "LLM,Vision"),
        ("gpt-4", "LLM,Coding,Reasoning"),
        ("gpt-3.5", "LLM,Coding"),
        ("o1", "LLM,Reasoning,Coding"),
        ("o3", "LLM,Reasoning,Coding"),
        ("o4-mini", "LLM,Reasoning,Coding,Vision"),
        ("dall-e", "ImageGen"),
        ("whisper", "Audio"),
        ("tts", "Audio"),

        // DeepSeek
        ("deepseek-r1", "LLM,Reasoning"),
        ("deepseek-coder", "LLM,Coding"),
        ("deepseek-chat", "LLM,Coding"),
        ("deepseek-v3", "LLM,Coding,Reasoning"),
        ("deepseek-v2", "LLM,Coding"),

        // 通义千问 (Qwen)
        ("qwen-vl", "LLM,Vision"),
        ("qwen-coder", "LLM,Coding"),
        ("qwen-math", "LLM,Reasoning,Science"),
        ("qwen-audio", "LLM,Audio"),
        ("qwen-plus", "LLM,Coding"),
        ("qwen-turbo", "LLM"),
        ("qwen-max", "LLM,Coding,Reasoning"),
        ("qwen2.5-coder", "LLM,Coding"),
        ("qwen2.5-vl", "LLM,Vision"),
        ("qwen2.5", "LLM,Coding"),
        ("qwen3", "LLM,Coding,Reasoning"),

        // 豆包 (Doubao)
        ("doubao-coder", "LLM,Coding"),
        ("doubao-vision", "LLM,Vision"),
        ("doubao", "LLM"),

        // Claude
        ("claude-3.5-sonnet", "LLM,Vision,Coding,Reasoning"),
        ("claude-3-opus", "LLM,Vision,Coding,Reasoning"),
        ("claude-3-sonnet", "LLM,Vision,Coding"),
        ("claude-3-haiku", "LLM,Vision"),
        ("claude", "LLM,Coding"),

        // Gemini
        ("gemini-2", "LLM,Vision,Coding,Reasoning,Multimodal"),
        ("gemini-1.5-pro", "LLM,Vision,Coding,Multimodal"),
        ("gemini-1.5-flash", "LLM,Vision,Multimodal"),
        ("gemini", "LLM,Vision"),

        // Ollama 常见模型
        ("llava", "LLM,Vision"),
        ("moondream", "Vision"),
        ("minicpm-v", "LLM,Vision"),
        ("codellama", "Coding"),
        ("codegemma", "Coding"),
        ("starcoder", "Coding"),
        ("stable-diffusion", "ImageGen"),
        ("sdxl", "ImageGen"),
        ("flux", "ImageGen"),
        ("wan2.1", "TextToVideo,ImageToVideo"),
        ("wan2.6", "TextToVideo,ImageToVideo"),
        ("cogvideo", "TextToVideo"),

        // 嵌入模型
        ("text-embedding", "Embedding"),
        ("bge-", "Embedding"),
        ("nomic-embed", "Embedding"),
        ("mxbai-embed", "Embedding"),

        // 翻译/写作
        ("nllb", "Translation"),
        ("aya", "Translation"),
    ];
}
