namespace OwnerAI.Host.Desktop.Models;

/// <summary>
/// 多模型编排总览 — 每个类别槽位的分配情况
/// </summary>
public sealed record ModelSlotSummary
{
    public required string Icon { get; init; }
    public required string CategoryName { get; init; }
    public required string AssignedModel { get; init; }
    public double AssignedOpacity => AssignedModel.StartsWith('(') ? 0.4 : 1.0;
}

/// <summary>
/// ComboBox 下拉项 — 显示文本 + 值
/// </summary>
public sealed record DisplayItem(string Display, string Value);
