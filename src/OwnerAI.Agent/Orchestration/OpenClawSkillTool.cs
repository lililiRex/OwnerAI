using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Orchestration;

/// <summary>
/// OpenClaw 技能桥接工具 — 让 Agent 能读取、应用和执行 OpenClaw 格式的外部技能。
/// 操作：list_skills / read_skill / log_learning / run_script
/// </summary>
[Tool("openclaw_skill",
    "管理和执行 OpenClaw 外部技能。可列出已安装技能、读取技能指南、记录学习日志、执行技能脚本。",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 30)]
public sealed class OpenClawSkillTool(
    IOpenClawSkillProvider skillProvider,
    ILogger<OpenClawSkillTool> logger) : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => skillProvider.GetSkills().Count > 0;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("action", out var actionEl))
            return ToolResult.Error("缺少参数: action (list_skills/read_skill/log_learning/run_script)");

        var action = actionEl.GetString();
        return action switch
        {
            "list_skills" => ListSkills(),
            "read_skill" => ReadSkill(parameters),
            "log_learning" => LogLearning(parameters),
            "run_script" => await RunScript(parameters, ct),
            _ => ToolResult.Error($"未知操作: {action}。可用: list_skills, read_skill, log_learning, run_script"),
        };
    }

    /// <summary>列出所有已安装的 OpenClaw 技能</summary>
    private ToolResult ListSkills()
    {
        var skills = skillProvider.GetSkills();
        if (skills.Count == 0)
            return ToolResult.Ok("未发现已安装的 OpenClaw 技能。\n安装路径: ~/.openclaw/skills/");

        var sb = new StringBuilder();
        sb.AppendLine($"已安装 {skills.Count} 个 OpenClaw 技能：\n");
        foreach (var skill in skills)
        {
            sb.AppendLine($"📦 **{skill.DisplayName}** (`{skill.Name}`) v{skill.Version} [{skill.Status}]");
            sb.AppendLine($"   {skill.Description}");
            if (skill.UsageCount > 0)
                sb.AppendLine($"   使用: {skill.UsageCount}次, 成功率: {skill.SuccessRate:P0}");
            if (skill.Scripts.Count > 0)
                sb.AppendLine($"   脚本: {string.Join(", ", skill.Scripts)}");
            sb.AppendLine($"   目录: {skill.SkillDirectory}");
            sb.AppendLine();
        }
        return ToolResult.Ok(sb.ToString());
    }

    /// <summary>读取技能的完整 SKILL.md 内容（注入知识）</summary>
    private ToolResult ReadSkill(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("skill_name", out var nameEl))
            return ToolResult.Error("缺少参数: skill_name");

        var name = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Error("skill_name 不能为空");

        var skill = skillProvider.FindSkill(name);
        if (skill is null)
        {
            var available = string.Join(", ", skillProvider.GetSkills().Select(s => s.Name));
            return ToolResult.Error($"未找到技能 '{name}'。已安装: {available}");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {skill.DisplayName} (`{skill.Name}`)");
        sb.AppendLine($"描述: {skill.Description}");
        sb.AppendLine($"目录: {skill.SkillDirectory}");
        if (skill.Scripts.Count > 0)
            sb.AppendLine($"可执行脚本: {string.Join(", ", skill.Scripts)}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // 截取 SKILL.md 内容（限制 token 消耗）
        var content = skill.FullContent ?? "(无内容)";
        if (content.Length > 8000)
            content = string.Concat(content.AsSpan(0, 8000), "\n\n... (内容过长，已截断)");
        sb.Append(content);

        return ToolResult.Ok(sb.ToString());
    }

    /// <summary>记录学习日志到 .learnings/ 目录</summary>
    private ToolResult LogLearning(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("type", out var typeEl))
            return ToolResult.Error("缺少参数: type (learning/error/feature_request)");
        if (!parameters.TryGetProperty("content", out var contentEl))
            return ToolResult.Error("缺少参数: content");

        var type = typeEl.GetString();
        var content = contentEl.GetString();
        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Error("content 不能为空");

        // 确定写入的文件
        var fileName = type switch
        {
            "error" => "ERRORS.md",
            "feature_request" => "FEATURE_REQUESTS.md",
            _ => "LEARNINGS.md",
        };

        // 写入 ~/.openclaw/workspace/.learnings/
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var learningsDir = Path.Combine(home, ".openclaw", "workspace", ".learnings");
        Directory.CreateDirectory(learningsDir);

        var filePath = Path.Combine(learningsDir, fileName);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var id = type switch
        {
            "error" => $"ERR-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmmss}",
            "feature_request" => $"FR-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmmss}",
            _ => $"LRN-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmmss}",
        };

        var entry = $"""

            ## [{id}] {timestamp}
            **Status**: active
            **Source**: OwnerAI Agent
            {content}

            """;

        File.AppendAllText(filePath, entry, Encoding.UTF8);
        logger.LogInformation("[OpenClawSkill] Logged {Type} to {File}: {Id}", type, fileName, id);

        return ToolResult.Ok($"已记录到 {filePath}\nID: {id}");
    }

    /// <summary>执行技能目录下的脚本</summary>
    private async Task<ToolResult> RunScript(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("skill_name", out var nameEl))
            return ToolResult.Error("缺少参数: skill_name");
        if (!parameters.TryGetProperty("script", out var scriptEl))
            return ToolResult.Error("缺少参数: script (脚本相对路径，如 scripts/activator.sh)");

        var name = nameEl.GetString();
        var scriptRelative = scriptEl.GetString();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(scriptRelative))
            return ToolResult.Error("skill_name 和 script 不能为空");

        var skill = skillProvider.FindSkill(name);
        if (skill is null)
            return ToolResult.Error($"未找到技能 '{name}'");

        if (!skill.Scripts.Contains(scriptRelative, StringComparer.OrdinalIgnoreCase))
            return ToolResult.Error($"技能 '{name}' 不包含脚本 '{scriptRelative}'。可用: {string.Join(", ", skill.Scripts)}");

        var scriptPath = Path.Combine(skill.SkillDirectory, scriptRelative);
        if (!File.Exists(scriptPath))
            return ToolResult.Error($"脚本文件不存在: {scriptPath}");

        try
        {
            var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
            var (fileName, arguments) = ext switch
            {
                ".ps1" => ("pwsh", $"-NoProfile -NonInteractive -File \"{scriptPath}\""),
                ".py" => ("python", $"\"{scriptPath}\""),
                ".js" => ("node", $"\"{scriptPath}\""),
                ".sh" => ("bash", $"\"{scriptPath}\""),
                _ => ("pwsh", $"-NoProfile -NonInteractive -File \"{scriptPath}\""),
            };

            // 追加用户自定义参数
            if (parameters.TryGetProperty("arguments", out var argsEl))
            {
                var extraArgs = argsEl.GetString();
                if (!string.IsNullOrWhiteSpace(extraArgs))
                    arguments += $" {extraArgs}";
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = skill.SkillDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return ToolResult.Error($"无法启动进程: {fileName}");

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var sb = new StringBuilder();
            sb.AppendLine($"[脚本: {scriptRelative}] [退出码: {process.ExitCode}]");
            if (!string.IsNullOrWhiteSpace(stdout))
                sb.AppendLine(stdout.Length > 5000 ? string.Concat(stdout.AsSpan(0, 5000), "\n...(已截断)") : stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
                sb.Append("[stderr] ").AppendLine(stderr);

            logger.LogInformation("[OpenClawSkill] Script {Script} exit={ExitCode}", scriptRelative, process.ExitCode);

            // 更新 skill.json 使用统计
            UpdateSkillUsage(skill.SkillDirectory, success: process.ExitCode == 0);

            return process.ExitCode == 0
                ? ToolResult.Ok(sb.ToString())
                : ToolResult.Error(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"脚本执行失败: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>更新 skill.json 中的使用/成功计数</summary>
    private static void UpdateSkillUsage(string skillDirectory, bool success)
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

            var usageCount = data.TryGetValue("usageCount", out var uc) && uc.TryGetInt32(out var ucVal) ? ucVal : 0;
            var successCount = data.TryGetValue("successCount", out var sc) && sc.TryGetInt32(out var scVal) ? scVal : 0;

            data["usageCount"] = JsonSerializer.SerializeToElement(usageCount + 1);
            if (success)
                data["successCount"] = JsonSerializer.SerializeToElement(successCount + 1);

            var json = JsonSerializer.Serialize(data, s_jsonOptions);
            File.WriteAllText(jsonPath, json, Encoding.UTF8);
        }
        catch { }
    }
}
