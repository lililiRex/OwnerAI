using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.ProcessTools;

/// <summary>
/// 应用启动工具 — 让 AI 能打开应用程序、文件或网址
/// </summary>
[Tool("open_app", "打开应用程序、文件或网址（例如打开浏览器、文件资源管理器、VS Code 等）",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 10)]
public sealed class OpenAppTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("target", out var targetEl))
            return ValueTask.FromResult(ToolResult.Error("缺少参数: target (要打开的程序/文件/网址)"));

        var target = targetEl.GetString();
        if (string.IsNullOrWhiteSpace(target))
            return ValueTask.FromResult(ToolResult.Error("target 不能为空"));

        var arguments = parameters.TryGetProperty("arguments", out var argsEl)
            ? argsEl.GetString()
            : null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            };

            if (!string.IsNullOrWhiteSpace(arguments))
                psi.Arguments = arguments;

            Process.Start(psi);
            return ValueTask.FromResult(ToolResult.Ok($"已打开: {target}" +
                (string.IsNullOrWhiteSpace(arguments) ? string.Empty : $" {arguments}")));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Error($"打开失败: {ex.Message}"));
        }
    }
}
