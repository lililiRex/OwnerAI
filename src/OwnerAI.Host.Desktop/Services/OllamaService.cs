using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// Ollama 本地模型管理服务 — 检测安装、部署模型、管理本地模型
/// </summary>
public static class OllamaService
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>可供选择的热门开源模型</summary>
    public static IReadOnlyList<OllamaModelOption> AvailableModels { get; } =
    [
        new("qwen3:8b",          "Qwen 3 8B",          "5.2 GB"),
        new("qwen2.5:7b",        "Qwen 2.5 7B",        "4.7 GB"),
        new("qwen2.5:14b",       "Qwen 2.5 14B",       "9.0 GB"),
        new("qwen2.5:32b",       "Qwen 2.5 32B",       "19 GB"),
        new("deepseek-r1:7b",    "DeepSeek R1 7B",      "4.7 GB",  SupportsTools: false),
        new("deepseek-r1:14b",   "DeepSeek R1 14B",     "9.0 GB",  SupportsTools: false),
        new("llama3.1:8b",       "Llama 3.1 8B",        "4.7 GB"),
        new("gemma3:12b",        "Gemma 3 12B",         "8.1 GB"),
        new("phi4:14b",          "Phi-4 14B",           "9.1 GB"),
        new("mistral:7b",        "Mistral 7B",          "4.1 GB"),
        new("codellama:7b",      "Code Llama 7B",       "3.8 GB"),
    ];

    /// <summary>检测 Ollama 是否已安装</summary>
    public static async Task<(bool Installed, string Version)> CheckInstalledAsync()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ollama", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return (false, string.Empty);

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0
                ? (true, output.Trim())
                : (false, string.Empty);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    /// <summary>检测 Ollama 服务是否正在运行</summary>
    public static async Task<bool> IsRunningAsync()
    {
        try
        {
            using var resp = await s_http.GetAsync("http://localhost:11434");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动 Ollama 服务</summary>
    public static async Task StartServerAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ollama", "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { /* 服务可能已在运行 */ }
        await Task.Delay(3000);
    }

    /// <summary>一键安装 Ollama（下载官方安装程序并运行）</summary>
    public static async Task InstallAsync(Action<string>? onStatus = null)
    {
        onStatus?.Invoke("正在下载 Ollama 安装程序...");
        var installerPath = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var bytes = await http.GetByteArrayAsync("https://ollama.com/download/OllamaSetup.exe");
        await File.WriteAllBytesAsync(installerPath, bytes);

        onStatus?.Invoke("正在运行安装程序...");
        var proc = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        if (proc is not null)
            await proc.WaitForExitAsync();

        onStatus?.Invoke("安装程序已结束，正在重新检测...");
    }

    /// <summary>获取本地已部署的模型列表</summary>
    public static async Task<List<string>> ListLocalModelsAsync()
    {
        try
        {
            var json = await s_http.GetStringAsync("http://localhost:11434/api/tags");
            using var doc = JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name))
                        models.Add(name.GetString() ?? "");
                }
            }
            return models;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>拉取（部署）模型</summary>
    public static async Task PullModelAsync(string modelName, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        using var proc = Process.Start(new ProcessStartInfo("ollama", $"pull {modelName}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动 ollama 进程");

        // ollama 将进度输出到 stderr，需要同时读取 stdout 和 stderr 防止死锁
        var stderrTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync(ct) is { } line)
                onProgress?.Invoke(line);
        }, ct);

        var stdoutTask = Task.Run(async () =>
        {
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
                onProgress?.Invoke(line);
        }, ct);

        await Task.WhenAll(stderrTask, stdoutTask);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ollama pull 失败 (exit code: {proc.ExitCode})");
    }

    /// <summary>删除本地已部署的模型</summary>
    public static async Task DeleteModelAsync(string modelName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "http://localhost:11434/api/delete")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { model = modelName }),
                Encoding.UTF8,
                "application/json")
        };
        using var resp = await s_http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>获取本地已部署的模型列表（含大小信息）</summary>
    public static async Task<List<OllamaLocalModel>> ListLocalModelsDetailedAsync()
    {
        try
        {
            var json = await s_http.GetStringAsync("http://localhost:11434/api/tags");
            using var doc = JsonDocument.Parse(json);
            var models = new List<OllamaLocalModel>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var size = m.TryGetProperty("size", out var s) ? FormatBytes(s.GetInt64()) : "";
                    var modified = m.TryGetProperty("modified_at", out var d) ? d.GetString() ?? "" : "";
                    models.Add(new OllamaLocalModel(name, size, modified));
                }
            }
            return models;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>搜索 Ollama 模型库中可部署的模型</summary>
    public static async Task<List<OllamaModelOption>> SearchLibraryAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [.. AvailableModels];

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ollama", $"search {query}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("无法启动 ollama");

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                return FilterLocalList(query);

            var results = ParseSearchOutput(output);
            return results.Count > 0 ? results : FilterLocalList(query);
        }
        catch
        {
            return FilterLocalList(query);
        }
    }

    private static List<OllamaModelOption> ParseSearchOutput(string output)
    {
        var results = new List<OllamaModelOption>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool headerSkipped = false;
        foreach (var line in lines)
        {
            if (!headerSkipped) { headerSkipped = true; continue; }
            var trimmed = line.TrimStart();
            var sep = trimmed.IndexOf("  ", StringComparison.Ordinal);
            var name = sep > 0 ? trimmed[..sep].Trim() : trimmed.Trim();
            if (!string.IsNullOrEmpty(name))
                results.Add(new OllamaModelOption(name, name, ""));
        }
        return results;
    }

    private static List<OllamaModelOption> FilterLocalList(string query) =>
        AvailableModels
            .Where(m => m.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || m.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F0} MB",
        _ => $"{bytes / 1024.0:F0} KB",
    };
}

/// <summary>可选的 Ollama 开源模型</summary>
public sealed record OllamaModelOption(string ModelId, string DisplayName, string Size, bool SupportsTools = true)
{
    /// <summary>ComboBox 显示文本</summary>
    public string Display => string.IsNullOrEmpty(Size) ? DisplayName : $"{DisplayName} ({Size})";
}

/// <summary>本地已安装的 Ollama 模型</summary>
public sealed record OllamaLocalModel(string Name, string Size, string ModifiedAt)
{
    /// <summary>ComboBox 显示文本</summary>
    public string Display => string.IsNullOrEmpty(Size) ? Name : $"{Name} ({Size})";
}
