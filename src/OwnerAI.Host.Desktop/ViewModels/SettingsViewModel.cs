using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenAI;
using OwnerAI.Configuration;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Host.Desktop.Services;

namespace OwnerAI.Host.Desktop.ViewModels;

/// <summary>
/// 设置页面 ViewModel — 主题、供应商配置、Agent 配置
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IThemeService _themeService;
    private readonly LocalSettingsService _settingsService;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    public ObservableCollection<ProviderConfigItem> Providers { get; } = [];

    /// <summary>
    /// Agent 配置列表 — 每个 Agent 是一组独立的人设/参数
    /// </summary>
    public ObservableCollection<AgentConfigItem> Agents { get; } = [];

    /// <summary>
    /// 多模型编排总览 — 展示各类别槽位的模型分配情况
    /// </summary>
    public ObservableCollection<ModelSlotSummary> ModelSlotSummaries { get; } = [];

    public ObservableCollection<DisplayItem> AgentModelOptions { get; } = [];

    /// <summary>
    /// 工作分类槽位分配 — 将系统中的具体工作流绑定到指定模型
    /// </summary>
    public ObservableCollection<WorkModelSlotItem> WorkModelSlots { get; } = [];

    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    public partial string VersionInfo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SaveStatus { get; set; } = string.Empty;

    private string _chatAgentModel = string.Empty;
    public string ChatAgentModel
    {
        get => _chatAgentModel;
        set => SetProperty(ref _chatAgentModel, value);
    }

    private double _chatAgentTemperature = 0.7;
    public double ChatAgentTemperature
    {
        get => _chatAgentTemperature;
        set => SetProperty(ref _chatAgentTemperature, value);
    }

    private string _evolutionAgentModel = string.Empty;
    public string EvolutionAgentModel
    {
        get => _evolutionAgentModel;
        set => SetProperty(ref _evolutionAgentModel, value);
    }

    private int _chatAgentMaxToolIterations = 15;
    public int ChatAgentMaxToolIterations
    {
        get => _chatAgentMaxToolIterations;
        set => SetProperty(ref _chatAgentMaxToolIterations, value);
    }

    private double _evolutionAgentTemperature = 0.3;
    public double EvolutionAgentTemperature
    {
        get => _evolutionAgentTemperature;
        set => SetProperty(ref _evolutionAgentTemperature, value);
    }

    private int _evolutionAgentMaxToolIterations = 30;
    public int EvolutionAgentMaxToolIterations
    {
        get => _evolutionAgentMaxToolIterations;
        set => SetProperty(ref _evolutionAgentMaxToolIterations, value);
    }

    // ── Ollama 本地模型 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOllamaInstall))]
    public partial bool IsOllamaInstalled { get; set; }

    [ObservableProperty]
    public partial bool IsOllamaRunning { get; set; }

    [ObservableProperty]
    public partial string OllamaStatusText { get; set; } = "正在检测...";

    [ObservableProperty]
    public partial OllamaModelOption? SelectedOllamaModel { get; set; }

    [ObservableProperty]
    public partial OllamaLocalModel? SelectedLocalModel { get; set; }

    [ObservableProperty]
    public partial bool IsOllamaOperating { get; set; }

    [ObservableProperty]
    public partial string OllamaProgress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AvailableModelSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CustomOllamaModelName { get; set; } = string.Empty;

    public ObservableCollection<OllamaLocalModel> LocalOllamaModels { get; } = [];

    public ObservableCollection<OllamaModelOption> AvailableOllamaModels { get; } = new(OllamaService.AvailableModels);

    public bool ShowOllamaInstall => !IsOllamaInstalled;

    public SettingsViewModel(IThemeService themeService, LocalSettingsService settingsService)
    {
        _themeService = themeService;
        _settingsService = settingsService;

        var settings = _settingsService.Load();

        // ── 加载供应商 ──
        foreach (var p in settings.Providers ?? [])
        {
            var item = new ProviderConfigItem
            {
                Name = p.Name,
                ProviderType = p.Type,
                Endpoint = p.Endpoint,
                ApiKey = p.ApiKey,
                ModelId = p.ModelId ?? string.Empty,
                Priority = p.Priority,
                Categories = p.Categories ?? "LLM",
                Role = p.Role ?? "Primary",
                SupportsTools = p.SupportsTools,
                ApiKeyPlaceholder = ProviderConfigItem.Presets
                    .FirstOrDefault(x => x.Type == p.Type)?.ApiKeyPlaceholder ?? "sk-...",
            };
            item.PropertyChanged += OnProviderPropertyChanged;
            Providers.Add(item);
        }

        if (Providers.Count == 0)
        {
            var defaultItem = new ProviderConfigItem { Priority = 0, IsEditing = true };
            defaultItem.ApplyPreset("阿里云百炼");
            defaultItem.PropertyChanged += OnProviderPropertyChanged;
            Providers.Add(defaultItem);
        }

        InitializeWorkModelSlots(settings);
        RefreshWorkModelSlotOptions();
        InitializeSimplifiedAgentSettings(settings);
        RefreshAgentModelOptions();

        // ── 加载 Agent（向后兼容旧格式）──
        if ((settings.Agents?.Count ?? 0) > 0)
        {
            foreach (var a in settings.Agents ?? [])
            {
                Agents.Add(new AgentConfigItem
                {
                    Name = a.Name,
                    Persona = a.Persona,
                    Temperature = a.Temperature,
                    MaxToolIterations = a.MaxToolIterations,
                    ContextWindowBudget = a.ContextWindowBudget,
                    IsDefault = a.IsDefault,
                });
            }
        }
        else
        {
            // 从旧格式迁移
            Agents.Add(new AgentConfigItem
            {
                Name = "通用助手",
                Persona = settings.Persona ?? "你是一个高效、专业的个人 AI 助手。",
                Temperature = settings.Temperature,
                IsDefault = true,
            });
        }

        // ── 外观 ──
        SelectedThemeIndex = settings.ThemeIndex;
        if (_themeService.CurrentTheme != IndexToTheme(SelectedThemeIndex))
        {
            _themeService.SetTheme(IndexToTheme(SelectedThemeIndex));
        }

        VersionInfo = $"OwnerAI v{typeof(App).Assembly.GetName().Version} | .NET {Environment.Version}";

        RefreshModelSlotSummaries();
        Providers.CollectionChanged += (_, _) =>
        {
            RefreshModelSlotSummaries();
            RefreshWorkModelSlotOptions();
            RefreshAgentModelOptions();
        };

        // Ollama 初始化
        SelectedOllamaModel = AvailableOllamaModels.FirstOrDefault();
        _ = CheckOllamaAsync();
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        _themeService.SetTheme(IndexToTheme(value));
    }

    private static ElementTheme IndexToTheme(int index) => index switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    [RelayCommand]
    private void AddProvider()
    {
        var item = new ProviderConfigItem
        {
            Priority = Providers.Count,
            IsEditing = true,
        };
        // 默认选择阿里云百炼（国内最易上手）
        item.ApplyPreset("阿里云百炼");
        item.PropertyChanged += OnProviderPropertyChanged;
        Providers.Add(item);
    }

    [RelayCommand]
    private void RemoveProvider(ProviderConfigItem? provider)
    {
        if (provider is not null)
        {
            provider.PropertyChanged -= OnProviderPropertyChanged;
            Providers.Remove(provider);
        }
    }

    [RelayCommand]
    private async Task TestProviderAsync(ProviderConfigItem? provider)
    {
        if (provider is null)
            return;

        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            provider.ConnectionTestStatus = "❌ 请先填写 API 端点。";
            return;
        }

        if (string.IsNullOrWhiteSpace(provider.ModelId))
        {
            provider.ConnectionTestStatus = "❌ 请先填写模型 ID。";
            return;
        }

        provider.IsConnectionTestRunning = true;
        provider.ConnectionTestStatus = "⏳ 正在测试模型调用...";

        try
        {
            using var client = CreateChatClient(provider);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(Microsoft.Extensions.AI.ChatRole.System, "你是一个连接测试助手。请只返回极简结果。"),
                    new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "请回复：OK")
                ],
                cancellationToken: cts.Token);

            var text = string.IsNullOrWhiteSpace(response.Text)
                ? "(空回复，但调用成功)"
                : Truncate(response.Text.Trim(), 80);

            provider.ConnectionTestStatus = $"✅ 调用成功：{text}";
            SaveStatus = $"已完成模型测试：{provider.Name}";
        }
        catch (OperationCanceledException)
        {
            provider.ConnectionTestStatus = "❌ 调用超时，请检查网络、端点或模型响应速度。";
            SaveStatus = $"模型测试超时：{provider.Name}";
        }
        catch (Exception ex)
        {
            provider.ConnectionTestStatus = $"❌ 调用失败：{Truncate(ex.Message, 120)}";
            SaveStatus = $"模型测试失败：{provider.Name}";
        }
        finally
        {
            provider.IsConnectionTestRunning = false;
        }
    }

    // ── Agent 管理 ──

    [RelayCommand]
    private void AddAgent()
    {
        Agents.Add(new AgentConfigItem
        {
            Name = $"Agent {Agents.Count + 1}",
            IsDefault = Agents.Count == 0,
        });
    }

    [RelayCommand]
    private void RemoveAgent(AgentConfigItem? agent)
    {
        if (agent is not null && Agents.Count > 1)
        {
            var wasDefault = agent.IsDefault;
            Agents.Remove(agent);
            // 确保始终有一个默认 Agent
            if (wasDefault && Agents.Count > 0 && !Agents.Any(a => a.IsDefault))
            {
                Agents[0].IsDefault = true;
            }
        }
    }

    /// <summary>
    /// 监听供应商属性变更 → 仅对编辑项自动填充预设 + 刷新编排总览
    /// </summary>
    private void OnProviderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is ProviderConfigItem item)
        {
            // 供应商类型变更 → 仅编辑模式下自动填充预设
            // 已保存的项 (IsEditing=false) 加载时 ComboBox 可能触发假变更，不响应
            if (e.PropertyName == nameof(ProviderConfigItem.ProviderType) && item.IsEditing)
            {
                item.ApplyPreset(item.ProviderType);
            }

            // 模型 ID 变更 → 自动识别类别
            if (e.PropertyName == nameof(ProviderConfigItem.ModelId) && item.IsEditing
                && !string.IsNullOrWhiteSpace(item.ModelId))
            {
                var detected = ProviderConfigItem.AutoDetectCategories(item.ModelId);
                if (detected != "LLM" || item.Categories == "LLM") // 仅在检测到特定类别或当前为默认值时覆盖
                {
                    item.Categories = detected;
                }
                item.SupportsTools = !IsNonToolModel(item.ModelId);
            }

            // 用户手动修改任何字段 → 标记为编辑模式（下次切类型时会触发预设填充）
            if (!item.IsEditing && e.PropertyName is
                nameof(ProviderConfigItem.ApiKey) or
                nameof(ProviderConfigItem.ModelId) or
                nameof(ProviderConfigItem.Endpoint) or
                nameof(ProviderConfigItem.Name))
            {
                item.IsEditing = true;
            }
        }

        if (e.PropertyName is nameof(ProviderConfigItem.Categories)
            or nameof(ProviderConfigItem.Role)
            or nameof(ProviderConfigItem.Name)
            or nameof(ProviderConfigItem.ProviderType)
            or nameof(ProviderConfigItem.ModelId))
        {
            RefreshModelSlotSummaries();
            RefreshWorkModelSlotOptions();
            RefreshAgentModelOptions();
        }
    }

    [RelayCommand]
    private void Save()
    {
        var settings = new LocalSettings
        {
            ThemeIndex = SelectedThemeIndex,
        };

        // 保存供应商
        foreach (var p in Providers)
        {
            settings.Providers.Add(new ProviderSetting
            {
                Name = p.Name,
                Type = p.ProviderType,
                Endpoint = p.Endpoint,
                ApiKey = p.ApiKey,
                ModelId = string.IsNullOrEmpty(p.ModelId) ? null : p.ModelId,
                Priority = p.Priority,
                Categories = p.Categories ?? "LLM",
                Role = p.Role ?? "Primary",
                SupportsTools = p.SupportsTools,
            });
            p.IsEditing = false;
        }

        // 保存 Agent
        foreach (var a in Agents)
        {
            settings.Agents.Add(new AgentSetting
            {
                Name = a.Name,
                Persona = a.Persona,
                Temperature = a.Temperature,
                MaxToolIterations = a.MaxToolIterations,
                ContextWindowBudget = a.ContextWindowBudget,
                IsDefault = a.IsDefault,
            });
        }

        foreach (var slot in WorkModelSlots)
        {
            if (!string.IsNullOrWhiteSpace(slot.AssignedProviderName))
            {
                settings.WorkModelAssignments.Add(new WorkModelAssignmentSetting
                {
                    WorkCategory = slot.SlotValue,
                    ProviderName = slot.AssignedProviderName,
                });
            }
        }

        settings.ChatAgentModel = string.IsNullOrWhiteSpace(ChatAgentModel) ? null : ChatAgentModel;
        settings.ChatAgentTemperature = ChatAgentTemperature;
        settings.ChatAgentMaxToolIterations = ChatAgentMaxToolIterations;
        settings.EvolutionAgentModel = string.IsNullOrWhiteSpace(EvolutionAgentModel) ? null : EvolutionAgentModel;
        settings.EvolutionAgentTemperature = EvolutionAgentTemperature;
        settings.EvolutionAgentMaxToolIterations = EvolutionAgentMaxToolIterations;

        // 向后兼容 — 将默认 Agent 的值写到顶层字段
        var defaultAgent = Agents.FirstOrDefault(a => a.IsDefault) ?? Agents.FirstOrDefault();
        if (defaultAgent is not null)
        {
            settings.Temperature = defaultAgent.Temperature;
            settings.Persona = defaultAgent.Persona;
        }

        _settingsService.Save(settings);
        SaveStatus = "✅ 设置已保存 (重启后生效)";
    }

    private void InitializeWorkModelSlots(LocalSettings settings)
    {
        WorkModelSlots.Clear();
        var saved = (settings.WorkModelAssignments ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.WorkCategory))
            .GroupBy(x => x.WorkCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().ProviderName, StringComparer.OrdinalIgnoreCase);

        foreach (var slot in s_workModelDefinitions)
        {
            WorkModelSlots.Add(new WorkModelSlotItem
            {
                SlotValue = slot.Value,
                SlotName = slot.Display,
                Description = slot.Description,
                AssignedProviderName = saved.TryGetValue(slot.Value, out var providerName) ? providerName : string.Empty,
            });
        }
    }

    private void RefreshWorkModelSlotOptions()
    {
        var options = Providers
            .Select(p => new DisplayItem($"{p.Name} · {p.ModelId}", p.RegistryName))
            .OrderBy(p => p.Display)
            .ToList();

        foreach (var slot in WorkModelSlots)
        {
            // 保存当前选中 — Clear() 会触发 ComboBox TwoWay 写回 null，需要在之后恢复
            var savedName = slot.AssignedProviderName;

            slot.ProviderOptions.Clear();
            slot.ProviderOptions.Add(new DisplayItem("(自动选择)", string.Empty));
            foreach (var option in options)
                slot.ProviderOptions.Add(option);

            // 恢复或校验保存的选择
            slot.AssignedProviderName = savedName.Length > 0 && slot.ProviderOptions.Any(x => x.Value == savedName)
                ? savedName
                : string.Empty;

            slot.RefreshSelectionBinding();
        }
    }

    private void InitializeSimplifiedAgentSettings(LocalSettings settings)
    {
        ChatAgentModel = settings.ChatAgentModel
            ?? settings.WorkModelAssignments.FirstOrDefault(x => x.WorkCategory == nameof(ModelWorkCategory.ChatDefault))?.ProviderName
            ?? string.Empty;
        ChatAgentTemperature = settings.ChatAgentTemperature;
        ChatAgentMaxToolIterations = settings.ChatAgentMaxToolIterations;

        EvolutionAgentModel = settings.EvolutionAgentModel
            ?? settings.WorkModelAssignments.FirstOrDefault(x => x.WorkCategory == nameof(ModelWorkCategory.EvolutionExecution))?.ProviderName
            ?? settings.WorkModelAssignments.FirstOrDefault(x => x.WorkCategory == nameof(ModelWorkCategory.EvolutionPlanning))?.ProviderName
            ?? string.Empty;
        EvolutionAgentTemperature = settings.EvolutionAgentTemperature;
        EvolutionAgentMaxToolIterations = settings.EvolutionAgentMaxToolIterations;
    }

    private void RefreshAgentModelOptions()
    {
        var selectedChat = ChatAgentModel;
        var selectedEvolution = EvolutionAgentModel;
        var options = Providers
            .Select(p => new DisplayItem($"{p.Name} · {p.ModelId}", p.RegistryName))
            .DistinctBy(p => p.Value)
            .OrderBy(p => p.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AgentModelOptions.Clear();
        AgentModelOptions.Add(new DisplayItem("(跟随自动选择)", string.Empty));
        foreach (var option in options)
            AgentModelOptions.Add(option);

        if (selectedChat.Length > 0 && !AgentModelOptions.Any(x => x.Value == selectedChat))
            ChatAgentModel = string.Empty;
        if (selectedEvolution.Length > 0 && !AgentModelOptions.Any(x => x.Value == selectedEvolution))
            EvolutionAgentModel = string.Empty;
    }

    private static readonly (string Value, string Display, string Description)[] s_workModelDefinitions =
    [
        (nameof(ModelWorkCategory.ChatDefault), "💬 默认聊天", "普通对话、常规问答、基础工具协作"),
        (nameof(ModelWorkCategory.ChatFast), "⚡ 快速聊天", "更偏响应速度的轻量对话槽位"),
        (nameof(ModelWorkCategory.CodeTask), "💻 代码任务", "代码分析、改代码、读写工程文件"),
        (nameof(ModelWorkCategory.DeepReasoning), "🧠 深度推理", "复杂分析、困难决策、非进化类后台任务"),
        (nameof(ModelWorkCategory.EvolutionPlanning), "🧭 进化规划", "能力缺口分解、Issue Tree 规划"),
        (nameof(ModelWorkCategory.EvolutionExecution), "🛠 进化执行", "按计划逐步实施与修复"),
        (nameof(ModelWorkCategory.EvolutionVerification), "🧪 进化验收", "测试、验收、Skill 形成与关闭缺口"),
        (nameof(ModelWorkCategory.VisionAssist), "👁️ 视觉辅助", "图片/多模态输入场景"),
    ];

    private static IChatClient CreateChatClient(ProviderConfigItem provider)
    {
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? null
            : new Uri(provider.Endpoint);

        var apiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? "unused" : provider.ApiKey;
        var modelId = string.IsNullOrWhiteSpace(provider.ModelId) ? "gpt-4o" : provider.ModelId;

        var options = new OpenAIClientOptions();
        if (endpoint is not null)
            options.Endpoint = endpoint;

        var openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), options);
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    [RelayCommand]
    private static void OpenLogsFolder()
    {
        var logsPath = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(logsPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logsPath,
                UseShellExecute = true,
            });
        }
    }

    // ── Ollama 管理 ──

    [RelayCommand]
    private async Task CheckOllamaAsync()
    {
        OllamaStatusText = "正在检测...";
        try
        {
            var (installed, version) = await OllamaService.CheckInstalledAsync();
            IsOllamaInstalled = installed;

            if (installed)
            {
                IsOllamaRunning = await OllamaService.IsRunningAsync();
                if (!IsOllamaRunning)
                {
                    OllamaStatusText = $"✅ 已安装 ({version}) — 正在启动服务...";
                    await OllamaService.StartServerAsync();
                    IsOllamaRunning = await OllamaService.IsRunningAsync();
                }

                OllamaStatusText = IsOllamaRunning
                    ? $"✅ 已安装 ({version}) — 服务运行中"
                    : $"✅ 已安装 ({version}) — 服务未运行";

                await RefreshLocalModelsAsync();
            }
            else
            {
                OllamaStatusText = "❌ 未安装 Ollama";
            }
        }
        catch
        {
            OllamaStatusText = "❌ 检测失败";
        }
    }

    [RelayCommand]
    private async Task InstallOllamaAsync()
    {
        IsOllamaOperating = true;
        OllamaProgress = string.Empty;
        try
        {
            await Task.Run(async () =>
                await OllamaService.InstallAsync(
                    status => _dispatcher.TryEnqueue(() => OllamaProgress = status)));
            await CheckOllamaAsync();
        }
        catch (Exception ex)
        {
            OllamaProgress = $"❌ 安装失败: {ex.Message}";
        }
        finally
        {
            IsOllamaOperating = false;
        }
    }

    [RelayCommand]
    private async Task PullOllamaModelAsync()
    {
        if (SelectedOllamaModel is null) return;

        IsOllamaOperating = true;
        OllamaProgress = $"正在部署 {SelectedOllamaModel.DisplayName}...";
        try
        {
            var modelId = SelectedOllamaModel.ModelId;
            await Task.Run(async () =>
                await OllamaService.PullModelAsync(
                    modelId,
                    progress => _dispatcher.TryEnqueue(() => OllamaProgress = progress)));

            OllamaProgress = $"✅ {SelectedOllamaModel.DisplayName} 部署完成";
            await RefreshLocalModelsAsync();
        }
        catch (Exception ex)
        {
            OllamaProgress = $"❌ 部署失败: {ex.Message}";
        }
        finally
        {
            IsOllamaOperating = false;
        }
    }

    [RelayCommand]
    private async Task PullCustomOllamaModelAsync()
    {
        var modelName = CustomOllamaModelName?.Trim();
        if (string.IsNullOrEmpty(modelName)) return;

        IsOllamaOperating = true;
        OllamaProgress = $"正在部署 {modelName}...";
        try
        {
            await Task.Run(async () =>
                await OllamaService.PullModelAsync(
                    modelName,
                    progress => _dispatcher.TryEnqueue(() => OllamaProgress = progress)));

            OllamaProgress = $"✅ {modelName} 部署完成";
            CustomOllamaModelName = string.Empty;
            await RefreshLocalModelsAsync();
        }
        catch (Exception ex)
        {
            OllamaProgress = $"❌ 部署失败: {ex.Message}";
        }
        finally
        {
            IsOllamaOperating = false;
        }
    }

    [RelayCommand]
    private async Task DeleteOllamaModelAsync()
    {
        if (SelectedLocalModel is null) return;

        IsOllamaOperating = true;
        var modelName = SelectedLocalModel.Name;
        OllamaProgress = $"正在卸载 {modelName}...";
        try
        {
            await OllamaService.DeleteModelAsync(modelName);
            OllamaProgress = $"✅ {modelName} 已卸载";
            await RefreshLocalModelsAsync();
        }
        catch (Exception ex)
        {
            OllamaProgress = $"❌ 卸载失败: {ex.Message}";
        }
        finally
        {
            IsOllamaOperating = false;
        }
    }

    [RelayCommand]
    private async Task SearchAvailableModelsAsync()
    {
        IsOllamaOperating = true;
        OllamaProgress = "正在搜索模型库...";
        try
        {
            var query = AvailableModelSearchText;
            var results = await Task.Run(async () => await OllamaService.SearchLibraryAsync(query));
            AvailableOllamaModels.Clear();
            foreach (var m in results)
                AvailableOllamaModels.Add(m);
            if (results.Count > 0)
                SelectedOllamaModel = results[0];
            OllamaProgress = $"找到 {results.Count} 个模型";
        }
        catch (Exception ex)
        {
            OllamaProgress = $"❌ 搜索失败: {ex.Message}";
        }
        finally
        {
            IsOllamaOperating = false;
        }
    }

    [RelayCommand]
    private void IntegrateOllamaModel()
    {
        if (SelectedLocalModel is null) return;
        var modelName = SelectedLocalModel.Name;

        // 检查是否已接入
        if (Providers.Any(p => p.ProviderType == "Ollama" && p.ModelId == modelName))
        {
            OllamaProgress = $"⚠️ {modelName} 已接入";
            return;
        }

        var item = new ProviderConfigItem
        {
            Priority = Providers.Count,
            IsEditing = true,
        };
        item.ApplyPreset("Ollama");
        item.ModelId = modelName;
        item.Name = $"Ollama ({modelName})";
        // 根据模型名称自动识别类别
        item.Categories = ProviderConfigItem.AutoDetectCategories(modelName);
        // 根据模型名称自动判断是否支持工具调用
        item.SupportsTools = !IsNonToolModel(modelName);
        item.PropertyChanged += OnProviderPropertyChanged;
        Providers.Add(item);

        OllamaProgress = $"✅ {modelName} 已接入为供应商，请保存设置";
    }

    private async Task RefreshLocalModelsAsync()
    {
        var models = await OllamaService.ListLocalModelsDetailedAsync();
        LocalOllamaModels.Clear();
        foreach (var m in models)
            LocalOllamaModels.Add(m);
        SelectedLocalModel = models.Count > 0 ? models[0] : null;
    }

    /// <summary>
    /// 刷新多模型编排总览表
    /// </summary>
    private void RefreshModelSlotSummaries()
    {
        ModelSlotSummaries.Clear();

        var slots = new (string Icon, string Category, string CategoryName)[]
        {
            ("🗣️", "LLM", "通用对话"),
            ("💻", "Coding", "代码专用"),
            ("🧠", "Reasoning", "深度推理"),
            ("👁️", "Vision", "视觉理解/图像识别"),
            ("🎨", "ImageGen", "文生图"),
            ("🎬", "ImageToVideo", "图生视频"),
            ("📹", "TextToVideo", "文生视频"),
            ("🔊", "Audio", "语音"),
            ("🌐", "Translation", "翻译"),
            ("✍️", "Writing", "写作/创意"),
            ("📊", "DataAnalysis", "数据分析"),
            ("📐", "Embedding", "嵌入向量"),
            ("🔬", "Science", "基础科学"),
            ("🎭", "Multimodal", "多模态"),
        };

        foreach (var (icon, cat, catName) in slots)
        {
            var matched = Providers
                .Where(p => (p.Categories ?? "LLM")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(c => c.Equals(cat, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            string assigned;
            if (matched.Count == 0)
            {
                assigned = "(未配置)";
            }
            else
            {
                var parts = matched.Select(p =>
                {
                    var role = (p.Role ?? "Primary").Equals("Primary", StringComparison.OrdinalIgnoreCase) ? "⭐主" : "📎次";
                    var name = string.IsNullOrWhiteSpace(p.ModelId)
                        ? p.Name
                        : $"[{p.ModelId}|{p.ProviderType}]";
                    return $"{role} {name}";
                });
                assigned = string.Join("  |  ", parts);
            }

            ModelSlotSummaries.Add(new ModelSlotSummary
            {
                Icon = icon,
                CategoryName = catName,
                AssignedModel = assigned,
            });
        }
    }

    /// <summary>
    /// 判断模型是否不支持 function calling（工具调用）
    /// </summary>
    private static bool IsNonToolModel(string modelId)
    {
        // DeepSeek R1 系列是纯推理模型，不支持工具调用
        return modelId.Contains("deepseek-r1", StringComparison.OrdinalIgnoreCase);
    }
}
