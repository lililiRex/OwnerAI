using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.FileSystem;

/// <summary>
/// 写入文件工具 — 让 AI 能创建和编辑文件（受路径安全校验保护）
/// </summary>
[Tool("write_file", "创建或覆盖文件，可用于编写代码、生成配置文件、保存文档等",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 15)]
public sealed class WriteFileTool : IOwnerAITool
{
    private static readonly string[] BlockedPaths =
    [
        @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)",
    ];

    private static readonly string[] BlockedExtensions =
    [
        ".exe", ".bat", ".cmd", ".vbs", ".reg", ".sys", ".dll",
    ];

    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("path", out var pathEl))
            return ToolResult.Error("缺少参数: path");
        if (!parameters.TryGetProperty("content", out var contentEl))
            return ToolResult.Error("缺少参数: content");

        var path = pathEl.GetString();
        var content = contentEl.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("文件路径不能为空");

        var fullPath = Path.GetFullPath(path);

        // 安全校验
        foreach (var blocked in BlockedPaths)
        {
            if (fullPath.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error($"禁止写入系统路径: {blocked}");
        }

        var ext = Path.GetExtension(fullPath);
        if (BlockedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return ToolResult.Error($"禁止写入可执行文件: {ext}");

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content, ct);
            return ToolResult.Ok($"文件已写入: {fullPath} ({content.Length} 字符)");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"写入文件失败: {ex.Message}");
        }
    }
}
