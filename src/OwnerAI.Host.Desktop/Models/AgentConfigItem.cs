using CommunityToolkit.Mvvm.ComponentModel;

namespace OwnerAI.Host.Desktop.Models;

/// <summary>
/// Agent 配置 UI 模型 — 每张卡片对应一个 Agent 实例
/// </summary>
public sealed partial class AgentConfigItem : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "通用助手";

    [ObservableProperty]
    public partial string Persona { get; set; } = "你是一个高效、专业的个人 AI 助手。";

    [ObservableProperty]
    public partial double Temperature { get; set; } = 0.7;

    [ObservableProperty]
    public partial int MaxToolIterations { get; set; } = 15;

    [ObservableProperty]
    public partial int ContextWindowBudget { get; set; } = 128_000;

    [ObservableProperty]
    public partial bool IsDefault { get; set; }
}
