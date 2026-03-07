using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Orchestration;

/// <summary>
/// 技能生命周期自动管理服务 — 定期检查试用期技能，自动晋升/淘汰。
/// <list type="bullet">
///   <item>trial → stable: usageCount ≥ 5 且 successRate ≥ 0.8</item>
///   <item>trial/stable → deprecated: usageCount ≥ 10 且 successRate &lt; 0.3</item>
/// </list>
/// </summary>
public sealed class SkillLifecycleService(
    IOpenClawSkillProvider skillProvider,
    ILogger<SkillLifecycleService> logger) : BackgroundService
{
    private const int PromoteMinUsage = 5;
    private const double PromoteMinSuccessRate = 0.8;
    private const int DeprecateMinUsage = 10;
    private const double DeprecateMaxSuccessRate = 0.3;

    private static readonly TimeSpan s_interval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待应用启动完成
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EvaluateSkillLifecycles();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SkillLifecycle] 生命周期检查失败");
            }

            await Task.Delay(s_interval, stoppingToken);
        }
    }

    private void EvaluateSkillLifecycles()
    {
        var skills = skillProvider.GetSkills();
        if (skills.Count == 0) return;

        foreach (var skill in skills)
        {
            var currentStatus = skill.Status;
            string? newStatus = null;

            if (currentStatus is "trial"
                && skill.UsageCount >= PromoteMinUsage
                && skill.SuccessRate >= PromoteMinSuccessRate)
            {
                newStatus = "stable";
                logger.LogInformation(
                    "[SkillLifecycle] 晋升技能 '{Name}': trial → stable (usage={Usage}, rate={Rate:P0})",
                    skill.Name, skill.UsageCount, skill.SuccessRate);
            }
            else if (currentStatus is "trial" or "stable"
                     && skill.UsageCount >= DeprecateMinUsage
                     && skill.SuccessRate < DeprecateMaxSuccessRate)
            {
                newStatus = "deprecated";
                logger.LogWarning(
                    "[SkillLifecycle] 淘汰技能 '{Name}': {Old} → deprecated (usage={Usage}, rate={Rate:P0})",
                    skill.Name, currentStatus, skill.UsageCount, skill.SuccessRate);
            }

            if (newStatus is not null)
                UpdateSkillStatus(skill.SkillDirectory, newStatus);
        }
    }

    private void UpdateSkillStatus(string skillDirectory, string newStatus)
    {
        try
        {
            var jsonPath = Path.Combine(skillDirectory, "skill.json");
            Dictionary<string, JsonElement>? data = null;

            if (File.Exists(jsonPath))
            {
                var existing = File.ReadAllText(jsonPath);
                data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing);
            }
            data ??= [];

            data["status"] = JsonSerializer.SerializeToElement(newStatus);
            data["statusUpdatedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.Now.ToString("o"));

            var json = JsonSerializer.Serialize(data, s_jsonOptions);
            File.WriteAllText(jsonPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SkillLifecycle] 更新技能状态失败: {Dir}", skillDirectory);
        }
    }
}
