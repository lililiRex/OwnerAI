using System.Text.Json;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 管理 Skills/ 插件目录中的技能，支持从外部目录手动导入。
/// 实现 IOpenClawSkillProvider 供 Agent 层读取技能内容并执行。
/// 启动时仅加载 Skills/ 目录，外部导入需用户手动触发。
/// </summary>
public sealed class OpenClawSkillScanner : IOpenClawSkillProvider
{
    private readonly SkillPluginManager _pluginManager;
    private readonly List<string> _searchPaths = [];
    private List<OpenClawSkillInfo>? _cachedSkills;

    public OpenClawSkillScanner(SkillPluginManager pluginManager)
    {
        _pluginManager = pluginManager;
    }

    /// <summary>添加额外的技能导入源目录</summary>
    public void AddSearchPath(string path)
    {
        if (!_searchPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            _searchPaths.Add(path);
        _cachedSkills = null;
    }

    /// <summary>清除缓存，下次访问时重新加载</summary>
    public void InvalidateCache() => _cachedSkills = null;

    // ── IOpenClawSkillProvider ──

    public IReadOnlyList<OpenClawSkillInfo> GetSkills()
    {
        _cachedSkills ??= ScanSkillInfos();
        return _cachedSkills;
    }

    public OpenClawSkillInfo? FindSkill(string name)
        => GetSkills().FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ── UI 用 ──

    /// <summary>返回 SkillItem 列表供技能页面展示</summary>
    public IReadOnlyList<SkillItem> Scan()
    {
        var skills = GetSkills();
        var results = new List<SkillItem>(skills.Count);
        foreach (var skill in skills)
        {
            results.Add(new SkillItem
            {
                Name = skill.Name,
                DisplayName = skill.DisplayNameCN ?? skill.DisplayName,
                Description = skill.DescriptionCN ?? skill.Description,
                Subtitle = skill.DisplayName,
                Glyph = "\uE946",
                Category = "OpenClaw 技能",
                SecurityLabel = "外部",
                SecurityColor = "#8B5CF6",
                IsExternal = true,
                SourceDirectory = skill.SkillDirectory,
            });
        }
        return results;
    }

    // ── 核心：导入 + 加载 ──

    private List<OpenClawSkillInfo> ScanSkillInfos()
    {
        // 仅当用户手动添加了搜索路径时才从外部导入
        if (_searchPaths.Count > 0)
            ImportFromSearchPaths();

        // 从 Skills/ 加载所有已安装的插件
        return _pluginManager.LoadAll();
    }

    /// <summary>扫描外部搜索路径，将新发现的技能导入到 Skills/ 目录</summary>
    private void ImportFromSearchPaths()
    {
        foreach (var basePath in _searchPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            // 检查 basePath 本身是否为技能目录
            TryImportSkillDir(basePath);

            // 扫描子目录
            foreach (var skillDir in Directory.GetDirectories(basePath))
                TryImportSkillDir(skillDir);
        }
    }

    /// <summary>尝试将一个 OpenClaw 技能目录导入到 Skills/</summary>
    private void TryImportSkillDir(string skillDir)
    {
        var skillMdPath = Path.Combine(skillDir, "SKILL.md");
        if (!File.Exists(skillMdPath)) return;
        try
        {
            var fullContent = File.ReadAllText(skillMdPath);
            var fm = ParseFrontmatter(fullContent);
            var name = fm.Name ?? Path.GetFileName(skillDir);

            // 已安装则跳过
            if (_pluginManager.IsInstalled(name)) return;

            var locale = LoadLocale(skillDir);
            var meta = LoadMeta(skillDir);

            // 英文名称/描述
            var displayName = ExtractFirstHeading(fullContent)
                              ?? FormatDisplayName(name);

            var description = fm.Description
                              ?? $"OpenClaw skill: {name}";

            // 中文名称/描述
            var nameCN = locale?.DisplayName
                         ?? meta?.DisplayName
                         ?? fm.NameZh;

            var descriptionCN = locale?.Description
                                ?? meta?.Description
                                ?? fm.DescriptionZh;

            _pluginManager.Import(skillDir, name, displayName, description, nameCN, descriptionCN);
        }
        catch { }
    }

    /// <summary>
    /// 尝试加载 _locale.zh.json 中文本地化
    /// </summary>
    private static SkillLocale? LoadLocale(string skillDir)
    {
        var path = Path.Combine(skillDir, "_locale.zh.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SkillLocale>(json);
        }
        catch { return null; }
    }

    /// <summary>
    /// 尝试加载 _meta.json 元数据（OpenClaw 标准格式）
    /// </summary>
    private static SkillLocale? LoadMeta(string skillDir)
    {
        var path = Path.Combine(skillDir, "_meta.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? title = null, desc = null;
            if (root.TryGetProperty("title_zh", out var tzh))
                title = tzh.GetString();
            else if (root.TryGetProperty("title", out var t))
                title = t.GetString();
            if (root.TryGetProperty("description_zh", out var dzh))
                desc = dzh.GetString();
            else if (root.TryGetProperty("description", out var d))
                desc = d.GetString();
            return (title ?? desc) is not null ? new SkillLocale { DisplayName = title, Description = desc } : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从 SKILL.md 正文提取第一个 # 标题作为显示名
    /// </summary>
    private static string? ExtractFirstHeading(string content)
    {
        // 跳过 frontmatter
        var body = content;
        if (content.StartsWith("---", StringComparison.Ordinal))
        {
            var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
            if (endIndex >= 0)
                body = content[(endIndex + 3)..];
        }
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var heading = line[2..].Trim();
                return string.IsNullOrEmpty(heading) ? null : heading;
            }
        }
        return null;
    }

    /// <summary>
    /// 解析 YAML frontmatter
    /// </summary>
    private static SkillFrontmatter ParseFrontmatter(string content)
    {
        var result = new SkillFrontmatter();
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return result;

        var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0) return result;

        var yaml = content[3..endIndex];
        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("name_zh:", StringComparison.OrdinalIgnoreCase))
                result.NameZh = StripYamlValue(line["name_zh:".Length..]);
            else if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                result.Name = StripYamlValue(line["name:".Length..]);
            else if (line.StartsWith("description_zh:", StringComparison.OrdinalIgnoreCase))
            {
                result.DescriptionZh = StripYamlValue(line["description_zh:".Length..]);
                if (result.DescriptionZh is not null && result.DescriptionZh.Length > 200)
                    result.DescriptionZh = string.Concat(result.DescriptionZh.AsSpan(0, 197), "...");
            }
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                result.Description = StripYamlValue(line["description:".Length..]);
                if (result.Description is not null && result.Description.Length > 200)
                    result.Description = string.Concat(result.Description.AsSpan(0, 197), "...");
            }
        }
        return result;
    }

    private static string? StripYamlValue(string raw)
    {
        var v = raw.Trim().Trim('"').Trim('\'').Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string FormatDisplayName(string name)
    {
        var parts = name.Split('-', '_');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        }
        return string.Join(' ', parts);
    }

    private sealed class SkillFrontmatter
    {
        public string? Name { get; set; }
        public string? NameZh { get; set; }
        public string? Description { get; set; }
        public string? DescriptionZh { get; set; }
    }

    private sealed class SkillLocale
    {
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Title { get; set; }
    }
}
