using System.Text.Json.Serialization;

namespace OwnerAI.Host.Desktop.Models;

/// <summary>
/// 技能插件清单 — skill.json 文件格式。
/// 存储在 Skills/{name}/skill.json，记录技能元数据，支持可移植分发。
/// </summary>
public sealed class SkillPlugin
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>中文名称</summary>
    public string? NameCN { get; set; }

    /// <summary>中文描述</summary>
    public string? DescriptionCN { get; set; }

    public string Version { get; set; } = "1.0.0";
    public string Source { get; set; } = "openclaw";
    public string? SourceHash { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SkillPlugin))]
internal sealed partial class SkillPluginJsonContext : JsonSerializerContext;
