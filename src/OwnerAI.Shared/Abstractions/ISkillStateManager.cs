namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 技能开关状态管理 — 允许用户启用/禁用特定工具
/// </summary>
public interface ISkillStateManager
{
    /// <summary>指定技能是否已启用</summary>
    bool IsEnabled(string toolName);

    /// <summary>设置技能启用/禁用状态</summary>
    void SetEnabled(string toolName, bool enabled);

    /// <summary>获取所有被禁用的技能名称</summary>
    IReadOnlySet<string> GetDisabledSkills();
}
