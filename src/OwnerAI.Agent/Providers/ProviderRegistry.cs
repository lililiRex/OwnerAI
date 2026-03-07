using Microsoft.Extensions.AI;
using OwnerAI.Configuration;

namespace OwnerAI.Agent.Providers;

/// <summary>
/// 模型供应商条目（支持多类别和角色）
/// </summary>
public sealed record ProviderEntry
{
    public required string Name { get; init; }
    public required IChatClient Client { get; init; }
    public int Priority { get; init; }

    /// <summary>模型类别列表 — 一个模型可属于多个类别</summary>
    public ModelCategory[] Categories { get; init; } = [ModelCategory.LLM];

    /// <summary>模型角色</summary>
    public ModelRole Role { get; init; } = ModelRole.Primary;

    /// <summary>模型是否支持 function calling (工具调用)</summary>
    public bool SupportsTools { get; init; } = true;

    /// <summary>供应商 API 端点 — 用于需要原生 API 调用的非聊天模型（如视频生成）</summary>
    public string? Endpoint { get; init; }

    /// <summary>API 密钥 — 用于原生 API 调用</summary>
    public string? ApiKey { get; init; }

    /// <summary>模型 ID — 用于原生 API 调用</summary>
    public string? ModelId { get; init; }

    /// <summary>检查模型是否属于指定类别</summary>
    public bool HasCategory(ModelCategory category) => Categories.Contains(category);
}

/// <summary>
/// 模型团队成员摘要 — 用于系统提示词注入
/// </summary>
public sealed record ModelTeamMember(
    string Name,
    ModelCategory[] Categories,
    ModelRole Role);

/// <summary>
/// 供应商注册表 — 管理主/次级模型的注册与查找
/// </summary>
public sealed class ProviderRegistry
{
    private readonly List<ProviderEntry> _providers = [];
    private readonly Dictionary<ModelWorkCategory, string> _workSlotAssignments = [];

    public void Register(ProviderEntry entry)
    {
        _providers.Add(entry);
        _providers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>获取所有注册的供应商</summary>
    public IReadOnlyList<ProviderEntry> GetAll() => _providers;

    /// <summary>获取所有主模型 — 用于主对话故障转移</summary>
    public IReadOnlyList<ProviderEntry> GetPrimaryProviders()
        => _providers.Where(p => p.Role == ModelRole.Primary).ToList();

    public IReadOnlyList<ProviderEntry> GetProvidersForWorkCategory(ModelWorkCategory workCategory)
    {
        if (_workSlotAssignments.TryGetValue(workCategory, out var providerName))
        {
            var assigned = GetByName(providerName);
            if (assigned is not null)
                return [assigned];
        }

        return workCategory switch
        {
            ModelWorkCategory.ChatDefault or ModelWorkCategory.ChatFast => GetPrimaryProviders(),
            ModelWorkCategory.CodeTask => GetProvidersByCategory(ModelCategory.Coding),
            ModelWorkCategory.DeepReasoning
                or ModelWorkCategory.EvolutionPlanning
                or ModelWorkCategory.EvolutionVerification => GetProvidersByCategory(ModelCategory.Reasoning),
            ModelWorkCategory.EvolutionExecution => GetProvidersByCategory(ModelCategory.Coding),
            ModelWorkCategory.VisionAssist => GetProvidersByCategory(ModelCategory.Vision),
            _ => GetPrimaryProviders(),
        };
    }

    /// <summary>获取所有次级模型 — 可被主模型调度的专业模型</summary>
    public IReadOnlyList<ProviderEntry> GetSecondaryProviders()
        => _providers.Where(p => p.Role == ModelRole.Secondary).ToList();

    public ProviderEntry? GetByName(string name)
        => _providers.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ProviderEntry? GetPrimary()
        => _providers.Find(p => p.Role == ModelRole.Primary) ?? (_providers.Count > 0 ? _providers[0] : null);

    /// <summary>获取指定类别的最高优先级模型 — 匹配 Categories 包含该类别的模型</summary>
    public ProviderEntry? GetByCategory(ModelCategory category)
        => _providers.Find(p => p.HasCategory(category));

    public IReadOnlyList<ProviderEntry> GetProvidersByCategory(ModelCategory category)
    {
        var matched = _providers.Where(p => p.HasCategory(category)).ToList();
        return matched.Count > 0 ? matched : GetPrimaryProviders();
    }

    public void ConfigureWorkSlots(IReadOnlyDictionary<ModelWorkCategory, string> assignments)
    {
        _workSlotAssignments.Clear();
        foreach (var pair in assignments)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
                _workSlotAssignments[pair.Key] = pair.Value;
        }
    }

    public IReadOnlyDictionary<ModelWorkCategory, string> GetWorkSlotAssignments()
        => _workSlotAssignments;

    /// <summary>获取所有已配置的模型类别</summary>
    public IReadOnlyList<ModelCategory> GetAvailableCategories()
        => _providers.SelectMany(p => p.Categories).Distinct().ToList();

    /// <summary>
    /// 获取模型团队成员列表 — 用于注入系统提示词，让主模型知道自己的团队
    /// </summary>
    public IReadOnlyList<ModelTeamMember> GetModelTeam()
        => _providers.Select(p => new ModelTeamMember(p.Name, p.Categories, p.Role)).ToList();

    /// <summary>
    /// 检查主模型是否支持 function calling (工具调用)
    /// </summary>
    public bool PrimarySupportsTools()
        => GetPrimary()?.SupportsTools ?? true;
}
