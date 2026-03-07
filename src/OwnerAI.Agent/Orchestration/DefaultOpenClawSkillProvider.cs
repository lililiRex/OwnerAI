using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Orchestration;

/// <summary>
/// 默认 OpenClaw 技能提供者 — 扫描 ~/.openclaw/skills/ 目录。
/// Desktop 宿主会用 OpenClawSkillScanner（支持本地化）覆盖此注册。
/// </summary>
internal sealed class DefaultOpenClawSkillProvider : IOpenClawSkillProvider
{
    private IReadOnlyList<OpenClawSkillInfo>? _cached;

    public IReadOnlyList<OpenClawSkillInfo> GetSkills()
    {
        _cached ??= ScanDefault();
        return _cached;
    }

    public OpenClawSkillInfo? FindSkill(string name)
        => GetSkills().FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static List<OpenClawSkillInfo> ScanDefault()
    {
        var results = new List<OpenClawSkillInfo>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var basePath = Path.Combine(home, ".openclaw", "skills");
        if (!Directory.Exists(basePath)) return results;

        foreach (var skillDir in Directory.GetDirectories(basePath))
        {
            var skillMdPath = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillMdPath)) continue;
            try
            {
                var content = File.ReadAllText(skillMdPath);
                var (name, description) = ParseMinimalFrontmatter(content);
                name ??= Path.GetFileName(skillDir);

                var scripts = new List<string>();
                var scriptsDir = Path.Combine(skillDir, "scripts");
                if (Directory.Exists(scriptsDir))
                {
                    foreach (var script in Directory.GetFiles(scriptsDir))
                    {
                        var ext = Path.GetExtension(script).ToLowerInvariant();
                        if (ext is ".sh" or ".ps1" or ".py" or ".js" or ".ts" or ".bat" or ".cmd")
                            scripts.Add(Path.GetRelativePath(skillDir, script));
                    }
                }

                var skillMeta = ReadSkillJson(skillDir);

                results.Add(new OpenClawSkillInfo
                {
                    Name = name,
                    DisplayName = name,
                    Description = description ?? $"OpenClaw skill: {name}",
                    SkillDirectory = skillDir,
                    FullContent = content,
                    Scripts = scripts,
                    Version = skillMeta.Version,
                    Source = skillMeta.Source,
                    Status = skillMeta.Status,
                    UsageCount = skillMeta.UsageCount,
                    SuccessCount = skillMeta.SuccessCount,
                    ImportedAt = skillMeta.ImportedAt,
                });
            }
            catch { }
        }
        return results;
    }

    private static (string? Name, string? Description) ParseMinimalFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return (null, null);
        var end = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, null);

        string? name = null, desc = null;
        foreach (var line in content[3..end].Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                name = t["name:".Length..].Trim().Trim('"', '\'').Trim();
            else if (t.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                desc = t["description:".Length..].Trim().Trim('"', '\'').Trim();
                if (desc.Length > 200) desc = string.Concat(desc.AsSpan(0, 197), "...");
            }
        }
        return (name, desc);
    }

    /// <summary>读取 skill.json 中的版本/状态/使用统计元数据</summary>
    private static SkillJsonMeta ReadSkillJson(string skillDir)
    {
        var jsonPath = Path.Combine(skillDir, "skill.json");
        if (!File.Exists(jsonPath))
            return new SkillJsonMeta();

        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            return new SkillJsonMeta
            {
                Version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0.0" : "1.0.0",
                Source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "manual" : "manual",
                Status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "stable" : "stable",
                UsageCount = root.TryGetProperty("usageCount", out var uc) && uc.TryGetInt32(out var ucVal) ? ucVal : 0,
                SuccessCount = root.TryGetProperty("successCount", out var sc) && sc.TryGetInt32(out var scVal) ? scVal : 0,
                ImportedAt = root.TryGetProperty("importedAt", out var ia) && DateTimeOffset.TryParse(ia.GetString(), out var iaVal) ? iaVal : null,
            };
        }
        catch
        {
            return new SkillJsonMeta();
        }
    }

    private sealed record SkillJsonMeta
    {
        public string Version { get; init; } = "1.0.0";
        public string Source { get; init; } = "manual";
        public string Status { get; init; } = "stable";
        public int UsageCount { get; init; }
        public int SuccessCount { get; init; }
        public DateTimeOffset? ImportedAt { get; init; }
    }
}
