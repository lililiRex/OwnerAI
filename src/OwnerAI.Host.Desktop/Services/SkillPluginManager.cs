using System.Security.Cryptography;
using System.Text.Json;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 技能插件管理器 — 管理 Skills/ 目录中的插件。
/// 负责加载、导入、删除技能插件，提供可移植的技能管理。
/// <para>目录结构: Skills/{name}/skill.json + SKILL.md + scripts/</para>
/// </summary>
public sealed class SkillPluginManager
{
    /// <summary>技能插件存储目录 (程序目录/Skills/)</summary>
    public string SkillsDirectory { get; }

    public SkillPluginManager()
    {
        SkillsDirectory = Path.Combine(AppContext.BaseDirectory, "Skills");
        Directory.CreateDirectory(SkillsDirectory);
    }

    /// <summary>加载所有已安装的技能插件并转换为 OpenClawSkillInfo</summary>
    public List<OpenClawSkillInfo> LoadAll()
    {
        var results = new List<OpenClawSkillInfo>();
        if (!Directory.Exists(SkillsDirectory)) return results;

        foreach (var skillDir in Directory.GetDirectories(SkillsDirectory))
        {
            var manifestPath = Path.Combine(skillDir, "skill.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var plugin = JsonSerializer.Deserialize(json, SkillPluginJsonContext.Default.SkillPlugin);
                if (plugin is null || string.IsNullOrWhiteSpace(plugin.Name)) continue;

                var skillMdPath = Path.Combine(skillDir, "SKILL.md");
                var fullContent = File.Exists(skillMdPath) ? File.ReadAllText(skillMdPath) : null;
                var scripts = ScanScripts(skillDir);

                results.Add(new OpenClawSkillInfo
                {
                    Name = plugin.Name,
                    DisplayName = plugin.DisplayName,
                    Description = plugin.Description,
                    DisplayNameCN = plugin.NameCN,
                    DescriptionCN = plugin.DescriptionCN,
                    SkillDirectory = skillDir,
                    FullContent = fullContent,
                    Scripts = scripts,
                });
            }
            catch { }
        }
        return results;
    }

    /// <summary>从 OpenClaw 源目录导入技能到 Skills/ 目录</summary>
    public bool Import(string sourceSkillDir, string name, string displayName, string description, string? nameCN = null, string? descriptionCN = null)
    {
        var targetDir = Path.Combine(SkillsDirectory, SanitizeName(name));
        if (Directory.Exists(targetDir)) return false;

        Directory.CreateDirectory(targetDir);

        // 复制 SKILL.md
        var srcSkillMd = Path.Combine(sourceSkillDir, "SKILL.md");
        if (File.Exists(srcSkillMd))
            File.Copy(srcSkillMd, Path.Combine(targetDir, "SKILL.md"));

        // 复制 scripts/
        var srcScriptsDir = Path.Combine(sourceSkillDir, "scripts");
        if (Directory.Exists(srcScriptsDir))
        {
            var dstScriptsDir = Path.Combine(targetDir, "scripts");
            Directory.CreateDirectory(dstScriptsDir);
            foreach (var file in Directory.GetFiles(srcScriptsDir))
                File.Copy(file, Path.Combine(dstScriptsDir, Path.GetFileName(file)));
        }

        // 复制本地化文件
        foreach (var localeFile in new[] { "_locale.zh.json", "_meta.json" })
        {
            var src = Path.Combine(sourceSkillDir, localeFile);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(targetDir, localeFile));
        }

        // 计算源文件哈希
        string? sourceHash = null;
        if (File.Exists(srcSkillMd))
        {
            var bytes = File.ReadAllBytes(srcSkillMd);
            var hash = SHA256.HashData(bytes);
            sourceHash = $"sha256:{Convert.ToHexStringLower(hash)}";
        }

        // 写入 skill.json 清单
        var plugin = new SkillPlugin
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            NameCN = nameCN,
            DescriptionCN = descriptionCN,
            Source = "openclaw",
            SourceHash = sourceHash,
            ImportedAt = DateTimeOffset.Now,
        };
        var json = JsonSerializer.Serialize(plugin, SkillPluginJsonContext.Default.SkillPlugin);
        File.WriteAllText(Path.Combine(targetDir, "skill.json"), json);

        return true;
    }

    /// <summary>删除已安装的技能插件（删除 Skills/ 下对应目录）</summary>
    public bool Delete(string name)
    {
        if (!Directory.Exists(SkillsDirectory)) return false;

        foreach (var skillDir in Directory.GetDirectories(SkillsDirectory))
        {
            var manifestPath = Path.Combine(skillDir, "skill.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var plugin = JsonSerializer.Deserialize(json, SkillPluginJsonContext.Default.SkillPlugin);
                if (plugin?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                {
                    Directory.Delete(skillDir, recursive: true);
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>检查技能是否已安装</summary>
    public bool IsInstalled(string name)
    {
        var targetDir = Path.Combine(SkillsDirectory, SanitizeName(name));
        return Directory.Exists(targetDir) && File.Exists(Path.Combine(targetDir, "skill.json"));
    }

    private static List<string> ScanScripts(string skillDir)
    {
        var scripts = new List<string>();
        var scriptsDir = Path.Combine(skillDir, "scripts");
        if (!Directory.Exists(scriptsDir)) return scripts;

        foreach (var script in Directory.GetFiles(scriptsDir))
        {
            var ext = Path.GetExtension(script).ToLowerInvariant();
            if (ext is ".sh" or ".ps1" or ".py" or ".js" or ".ts" or ".bat" or ".cmd")
                scripts.Add(Path.GetRelativePath(skillDir, script));
        }
        return scripts;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid));
    }
}
