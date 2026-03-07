using System.Text.Json;
using System.Text.Json.Serialization;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 本地设置持久化 — JSON 文件存储，API 密钥通过 DPAPI 加密
/// </summary>
public sealed class LocalSettingsService(ISecretStore secretStore)
{
    private const string ApiKeySecretPrefix = "provider_apikey_";
    private const string EncryptedMarker = "***ENCRYPTED***";

    private readonly string _settingsPath =
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public LocalSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return new LocalSettings();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize(json, LocalSettingsJsonContext.Default.LocalSettings)
                   ?? new LocalSettings();

            // 从 DPAPI 加密存储中恢复 API 密钥
            RestoreApiKeys(settings);
            return settings;
        }
        catch
        {
            return new LocalSettings();
        }
    }

    public void Save(LocalSettings settings)
    {
        // 将 API 密钥加密到 DPAPI 存储，settings.json 中只保留标记
        var clone = CloneWithEncryptedKeys(settings);

        var json = JsonSerializer.Serialize(clone, LocalSettingsJsonContext.Default.LocalSettings);
        var dir = Path.GetDirectoryName(_settingsPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(_settingsPath, json);
    }

    private void RestoreApiKeys(LocalSettings settings)
    {
        foreach (var provider in settings.Providers)
        {
            if (provider.ApiKey == EncryptedMarker || string.IsNullOrEmpty(provider.ApiKey))
            {
                var secretKey = $"{ApiKeySecretPrefix}{provider.Name}";
                var decrypted = secretStore.GetAsync(secretKey).GetAwaiter().GetResult();
                if (decrypted is not null)
                    provider.ApiKey = decrypted;
            }
            else if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                // 明文密钥 — 首次运行迁移到加密存储
                var secretKey = $"{ApiKeySecretPrefix}{provider.Name}";
                secretStore.SetAsync(secretKey, provider.ApiKey).GetAwaiter().GetResult();
            }
        }
    }

    private LocalSettings CloneWithEncryptedKeys(LocalSettings settings)
    {
        // 保存时加密所有 API 密钥
        var providers = new List<ProviderSetting>();
        foreach (var p in settings.Providers)
        {
            if (!string.IsNullOrWhiteSpace(p.ApiKey) && p.ApiKey != EncryptedMarker)
            {
                var secretKey = $"{ApiKeySecretPrefix}{p.Name}";
                secretStore.SetAsync(secretKey, p.ApiKey).GetAwaiter().GetResult();
            }

            providers.Add(new ProviderSetting
            {
                Name = p.Name,
                Type = p.Type,
                Endpoint = p.Endpoint,
                ApiKey = !string.IsNullOrWhiteSpace(p.ApiKey) && p.ApiKey != "unused"
                    ? EncryptedMarker
                    : p.ApiKey,
                ModelId = p.ModelId,
                Priority = p.Priority,
                Categories = p.Categories,
                Role = p.Role,
                SupportsTools = p.SupportsTools,
            });
        }

        return new LocalSettings
        {
            Providers = providers,
            Agents = settings.Agents,
            WorkModelAssignments = settings.WorkModelAssignments,
            ChatAgentModel = settings.ChatAgentModel,
            ChatAgentTemperature = settings.ChatAgentTemperature,
            ChatAgentMaxToolIterations = settings.ChatAgentMaxToolIterations,
            EvolutionAgentModel = settings.EvolutionAgentModel,
            EvolutionAgentTemperature = settings.EvolutionAgentTemperature,
            EvolutionAgentMaxToolIterations = settings.EvolutionAgentMaxToolIterations,
            ThemeIndex = settings.ThemeIndex,
            DisabledSkills = settings.DisabledSkills,
            DefaultModel = settings.DefaultModel,
            Temperature = settings.Temperature,
            Persona = settings.Persona,
        };
    }
}

/// <summary>
/// 本地设置模型
/// </summary>
public sealed class LocalSettings
{
    public List<ProviderSetting> Providers { get; set; } = [];
    public List<AgentSetting> Agents { get; set; } = [];
    public List<WorkModelAssignmentSetting> WorkModelAssignments { get; set; } = [];
    public string? ChatAgentModel { get; set; }
    public double ChatAgentTemperature { get; set; } = 0.7;
    public int ChatAgentMaxToolIterations { get; set; } = 15;
    public string? EvolutionAgentModel { get; set; }
    public double EvolutionAgentTemperature { get; set; } = 0.3;
    public int EvolutionAgentMaxToolIterations { get; set; } = 30;
    public int ThemeIndex { get; set; }

    /// <summary>已禁用的技能名称列表</summary>
    public List<string> DisabledSkills { get; set; } = [];

    // ── 向后兼容旧格式（已迁移到 AgentSetting）──
    public string? DefaultModel { get; set; }
    public double Temperature { get; set; } = 0.7;
    public string? Persona { get; set; }
}

/// <summary>
/// Agent 持久化模型
/// </summary>
public sealed class AgentSetting
{
    public string Name { get; set; } = "通用助手";
    public string Persona { get; set; } = "你是一个高效、专业的个人 AI 助手。";
    public double Temperature { get; set; } = 0.7;
    public int MaxToolIterations { get; set; } = 15;
    public int ContextWindowBudget { get; set; } = 128_000;
    public bool IsDefault { get; set; }
}

/// <summary>
/// 供应商持久化模型
/// </summary>
public sealed class ProviderSetting
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "OpenAI";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string? ModelId { get; set; }
    public int Priority { get; set; }

    /// <summary>模型类别（逗号分隔，一个模型可属于多个类别）: LLM,Vision,Coding</summary>
    public string Categories { get; set; } = "LLM";

    /// <summary>模型角色: primary, secondary</summary>
    public string Role { get; set; } = "primary";

    /// <summary>模型是否支持 function calling (工具调用)</summary>
    public bool SupportsTools { get; set; } = true;
}

/// <summary>
/// 工作分类槽位 → 模型分配 持久化模型
/// </summary>
public sealed class WorkModelAssignmentSetting
{
    public string WorkCategory { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
}

[JsonSerializable(typeof(LocalSettings))]
internal sealed partial class LocalSettingsJsonContext : JsonSerializerContext;
