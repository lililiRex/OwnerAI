namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 默认技能状态管理器 — 所有技能始终启用（用于 CLI 等无 UI 宿主）
/// </summary>
public sealed class NullSkillStateManager : ISkillStateManager
{
    private static readonly IReadOnlySet<string> s_empty = new HashSet<string>();

    public bool IsEnabled(string toolName) => true;
    public void SetEnabled(string toolName, bool enabled) { }
    public IReadOnlySet<string> GetDisabledSkills() => s_empty;
}
