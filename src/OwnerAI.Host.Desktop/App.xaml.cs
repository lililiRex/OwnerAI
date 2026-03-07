using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.UI.Xaml;
using OpenAI;
using OwnerAI.Agent;
using OwnerAI.Agent.Providers;
using OwnerAI.Agent.Tools;
using OwnerAI.Configuration;
using OwnerAI.Gateway;
using OwnerAI.Host.Desktop.Services;
using OwnerAI.Host.Desktop.ViewModels;
using OwnerAI.Host.Desktop.Views;
using OwnerAI.Security;
using OwnerAI.Shared.Abstractions;
using Serilog;

namespace OwnerAI.Host.Desktop;

public partial class App : Application
{
    private readonly IHost _host;

    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>主窗口引用 — 供文件选择器等需要窗口句柄的组件使用</summary>
    public static Microsoft.UI.Xaml.Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "ownerai-desktop-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        // 全局异常捕获
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((ctx, services) =>
            {
                // 技能插件管理（必须在 OpenClawSkillScanner 之前注册）
                services.AddSingleton<SkillPluginManager>();

                // OpenClaw 技能扫描（必须在 AddOwnerAIAgent 之前注册，以覆盖默认提供者）
                services.AddSingleton<OpenClawSkillScanner>();
                services.AddSingleton<IOpenClawSkillProvider>(sp => sp.GetRequiredService<OpenClawSkillScanner>());

                // 技能开关管理（必须在 AddOwnerAIAgent 之前注册，以覆盖默认 NullSkillStateManager）
                services.AddSingleton<ISkillStateManager, SkillStateManager>();

                // 核心服务
                services.AddOwnerAIGateway(ctx.Configuration);
                services.AddOwnerAIAgent(ctx.Configuration);
                services.AddOwnerAITools();
                services.AddOwnerAISecurity(ctx.Configuration);

                // 桌面审批服务覆盖 CLI 版本 — 使用 ContentDialog
                services.AddSingleton<IApprovalService, DesktopApprovalService>();

                // 用 settings.json 中的 Agent 配置覆盖默认 AgentConfig
                services.AddSingleton<IOptions<OwnerAIConfig>>(sp =>
                {
                    var settingsService = sp.GetRequiredService<LocalSettingsService>();
                    var settings = settingsService.Load();
                    var defaultAgent = settings.Agents.FirstOrDefault(a => a.IsDefault)
                        ?? settings.Agents.FirstOrDefault();

                    return Options.Create(new OwnerAIConfig
                    {
                        Agent = new AgentConfig
                        {
                            Temperature = (float)(defaultAgent?.Temperature ?? settings.Temperature),
                            Persona = defaultAgent?.Persona ?? settings.Persona ?? "你是一个高效、专业的个人 AI 助手。",
                            MaxToolIterations = defaultAgent?.MaxToolIterations ?? 15,
                            ContextWindowTokenBudget = defaultAgent?.ContextWindowBudget ?? 128_000,
                            WorkModelAssignments = ParseWorkAssignments(settings),
                        },
                        ChatAgent = new AgentRoleConfig
                        {
                            DisplayName = "ChatAgent",
                            DefaultModel = string.IsNullOrWhiteSpace(settings.ChatAgentModel) ? null : settings.ChatAgentModel,
                            Temperature = (float)settings.ChatAgentTemperature,
                            MaxToolIterations = settings.ChatAgentMaxToolIterations,
                        },
                        EvolutionAgent = new AgentRoleConfig
                        {
                            DisplayName = "EvolutionAgent",
                            DefaultModel = string.IsNullOrWhiteSpace(settings.EvolutionAgentModel) ? null : settings.EvolutionAgentModel,
                            Temperature = (float)settings.EvolutionAgentTemperature,
                            MaxToolIterations = settings.EvolutionAgentMaxToolIterations,
                        },
                    });
                });

                // UI 服务
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<LocalSettingsService>();
                services.AddSingleton<TrayIconService>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<ChatViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<SchedulerViewModel>();
                services.AddSingleton<SkillsViewModel>();
                services.AddTransient<MetricsViewModel>();

                // Views (按需创建)
                services.AddTransient<MainWindow>();
                services.AddTransient<ChatPage>();
                services.AddTransient<SettingsPage>();
                services.AddTransient<SchedulerPage>();
                services.AddTransient<SkillsPage>();
                services.AddTransient<MetricsPage>();
            })
            .Build();

        Services = _host.Services;

        // 从本地设置初始化 LLM 供应商
        InitializeProviders();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await _host.StartAsync();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        var trayIconService = Services.GetRequiredService<TrayIconService>();
        trayIconService.Initialize(mainWindow);
        mainWindow.Closed += (_, _) => trayIconService.Dispose();
        mainWindow.Activate();
    }

    /// <summary>
    /// 读取 settings.json 中配置的供应商，注册到 ProviderRegistry
    /// </summary>
    private static void InitializeProviders()
    {
        var settingsService = Services.GetRequiredService<LocalSettingsService>();
        var registry = Services.GetRequiredService<ProviderRegistry>();
        var settings = settingsService.Load();

        foreach (var provider in settings.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Endpoint) && string.IsNullOrWhiteSpace(provider.ApiKey))
                continue;

            try
            {
                var client = CreateChatClient(provider);
                if (client is not null)
                {
                    var displayName = string.IsNullOrWhiteSpace(provider.ModelId)
                        ? provider.Name
                        : $"[{provider.ModelId}|{provider.Type}]";

                    registry.Register(new ProviderEntry
                    {
                        Name = displayName,
                        Client = client,
                        Priority = provider.Priority,
                        Categories = ParseCategories(provider.Categories),
                        Role = ParseRole(provider.Role),
                        SupportsTools = provider.SupportsTools,
                        Endpoint = provider.Endpoint,
                        ApiKey = provider.ApiKey,
                        ModelId = provider.ModelId,
                    });
                    Log.Information("Registered provider: {Name} ({Type}) → {Endpoint} [{Categories}/{Role}]",
                        provider.Name, provider.Type, provider.Endpoint, provider.Categories, provider.Role);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize provider {Name}", provider.Name);
            }
        }

        registry.ConfigureWorkSlots(ParseWorkAssignments(settings));

        if (registry.GetAll().Count == 0)
        {
            Log.Warning("No LLM providers configured. Please configure in Settings.");
        }
        else
        {
            var primary = registry.GetPrimaryProviders();
            var secondary = registry.GetSecondaryProviders();
            Log.Information("Model team: {PrimaryCount} primary, {SecondaryCount} secondary",
                primary.Count, secondary.Count);
        }
    }

    private static IChatClient? CreateChatClient(ProviderSetting provider)
    {
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? null
            : new Uri(provider.Endpoint);

        var apiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? "unused" : provider.ApiKey;
        var modelId = string.IsNullOrWhiteSpace(provider.ModelId) ? "gpt-4o" : provider.ModelId;

        var options = new OpenAIClientOptions();
        if (endpoint is not null)
        {
            options.Endpoint = endpoint;
        }

        var openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), options);
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }

    private static OwnerAI.Configuration.ModelCategory[] ParseCategories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [OwnerAI.Configuration.ModelCategory.LLM];

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<OwnerAI.Configuration.ModelCategory>();
        foreach (var part in parts)
        {
            if (Enum.TryParse<OwnerAI.Configuration.ModelCategory>(part, ignoreCase: true, out var cat))
                result.Add(cat);
        }
        return result.Count > 0 ? result.ToArray() : [OwnerAI.Configuration.ModelCategory.LLM];
    }

    private static OwnerAI.Configuration.ModelRole ParseRole(string? value) => value?.ToLowerInvariant() switch
    {
        "secondary" or "sub" => OwnerAI.Configuration.ModelRole.Secondary,
        _ => OwnerAI.Configuration.ModelRole.Primary,
    };

    // ── 全局异常处理 ──

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "[UnhandledException] {Message}", e.Message);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "[UnobservedTaskException] 未观察的异步异常");
        e.SetObserved();
    }

    private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "[AppDomain.UnhandledException] IsTerminating={IsTerminating}", e.IsTerminating);
        else
            Log.Fatal("[AppDomain.UnhandledException] Non-exception object: {Object}, IsTerminating={IsTerminating}", e.ExceptionObject, e.IsTerminating);
    }

    private static Dictionary<ModelWorkCategory, string> ParseWorkAssignments(LocalSettings settings)
    {
        var result = new Dictionary<ModelWorkCategory, string>();
        foreach (var item in settings.WorkModelAssignments)
        {
            if (Enum.TryParse<ModelWorkCategory>(item.WorkCategory, ignoreCase: true, out var slot)
                && !string.IsNullOrWhiteSpace(item.ProviderName))
            {
                result[slot] = item.ProviderName;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ChatAgentModel))
        {
            result[ModelWorkCategory.ChatDefault] = settings.ChatAgentModel;
            result[ModelWorkCategory.ChatFast] = settings.ChatAgentModel;
        }

        if (!string.IsNullOrWhiteSpace(settings.EvolutionAgentModel))
        {
            result[ModelWorkCategory.EvolutionPlanning] = settings.EvolutionAgentModel;
            result[ModelWorkCategory.EvolutionExecution] = settings.EvolutionAgentModel;
            result[ModelWorkCategory.EvolutionVerification] = settings.EvolutionAgentModel;
        }

        return result;
    }
}
