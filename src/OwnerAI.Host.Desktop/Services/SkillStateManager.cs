using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 技能开关管理 — 持久化到 LocalSettings，运行时内存缓存
/// </summary>
public sealed class SkillStateManager : ISkillStateManager
{
    private readonly LocalSettingsService _settingsService;
    private readonly HashSet<string> _disabled;

    public SkillStateManager(LocalSettingsService settingsService)
    {
        _settingsService = settingsService;
        var settings = settingsService.Load();
        _disabled = new HashSet<string>(settings.DisabledSkills, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnabled(string toolName) => !_disabled.Contains(toolName);

    public void SetEnabled(string toolName, bool enabled)
    {
        if (enabled)
            _disabled.Remove(toolName);
        else
            _disabled.Add(toolName);

        // 持久化
        var settings = _settingsService.Load();
        settings.DisabledSkills = [.. _disabled];
        _settingsService.Save(settings);
    }

    public IReadOnlySet<string> GetDisabledSkills() => _disabled;
}
