using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;

namespace OwnerAI.Agent.Memory;

/// <summary>
/// Mem0 服务管理器 — 负责本地 Mem0 向量数据库的安装、启动、健康检查
/// <para>安装结构:</para>
/// <code>
/// D:\AIMem\
///   ├── venv\              # Python 虚拟环境
///   ├── mem0_server.py     # REST API 服务脚本
///   ├── mem0_config.json   # Mem0 运行配置
///   └── data\              # 向量数据存储目录
/// </code>
/// </summary>
public sealed class Mem0ServerManager : IDisposable
{
    private readonly Mem0Config _config;
    private readonly ILogger<Mem0ServerManager> _logger;
    private readonly HttpClient _httpClient;
    private Process? _serverProcess;

    /// <summary>Mem0 服务是否已就绪</summary>
    public bool IsReady { get; private set; }

    /// <summary>是否正在执行环境检查/安装</summary>
    public bool IsSettingUp { get; private set; }

    /// <summary>当前安装阶段描述（供 UI 显示）</summary>
    public string? SetupStatusMessage { get; private set; }

    /// <summary>安装阶段发生变化时触发 — 传递阶段描述文本</summary>
    public event Action<string>? OnSetupProgress;

    public Mem0ServerManager(IOptions<Mem0Config> config, ILogger<Mem0ServerManager> logger)
    {
        _config = config.Value;
        _logger = logger;
        _httpClient = new HttpClient { BaseAddress = new Uri(_config.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
    }

    private void ReportProgress(string message)
    {
        SetupStatusMessage = message;
        _logger.LogInformation("[Mem0] {Message}", message);
        OnSetupProgress?.Invoke(message);
    }

    /// <summary>
    /// 完整初始化流程 — 检查 → 安装 → 启动 → 健康检查
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsSettingUp = true;

        try
        {
            ReportProgress("正在检查 Mem0 服务状态...");

            // 1. 检查是否已在运行
            if (await IsServerRunningAsync(ct))
            {
                ReportProgress("Mem0 服务已在运行");
                IsReady = true;
                return;
            }

            // 2. 检查 Python 环境 — 未找到时尝试自动安装
            ReportProgress("正在检查 Python 环境...");
            var pythonPath = await FindPythonAsync(ct);
            if (pythonPath is null)
            {
                ReportProgress("未找到 Python 3.10+，正在尝试自动安装...");
                if (await TryInstallPythonAsync(ct))
                {
                    // 安装后重新检测
                    pythonPath = await FindPythonAsync(ct);
                }

                if (pythonPath is null)
                {
                    ReportProgress("❌ Python 安装失败 — Mem0 将使用内存缓存模式");
                    return;
                }
            }
            ReportProgress($"已找到 Python: {pythonPath}");

            // 3. 检查/创建安装目录
            ReportProgress("正在准备安装目录...");
            EnsureDirectories();

            // 4. 检查/创建虚拟环境
            var venvPython = GetVenvPythonPath();
            if (!File.Exists(venvPython))
            {
                ReportProgress("正在创建 Python 虚拟环境...");
                await CreateVenvAsync(pythonPath, ct);
            }

            // 5. 检查/安装 mem0ai 包
            if (!await IsMem0InstalledAsync(ct))
            {
                ReportProgress("正在安装 mem0ai 及依赖 (首次安装可能需要几分钟)...");
                await InstallMem0PackagesAsync(ct);
                ReportProgress("mem0ai 安装完成");
            }

            // 6. 生成服务脚本和配置
            ReportProgress("正在生成服务配置...");
            await WriteServerScriptAsync(ct);
            await WriteConfigAsync(ct);

            // 7. 启动服务
            ReportProgress("正在启动 Mem0 服务...");
            await StartServerAsync(ct);

            // 8. 等待服务就绪
            ReportProgress("正在等待 Mem0 服务就绪...");
            var ready = await WaitForServerReadyAsync(ct);
            IsReady = ready;

            ReportProgress(ready
                ? "✅ Mem0 服务已就绪"
                : "❌ Mem0 服务启动超时 — 使用内存缓存模式");
        }
        finally
        {
            IsSettingUp = false;
        }
    }

    /// <summary>
    /// 检查 Mem0 服务是否正在运行
    /// </summary>
    public async Task<bool> IsServerRunningAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 停止 Mem0 服务进程
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_serverProcess is { HasExited: false })
        {
            _logger.LogInformation("[Mem0] 正在停止服务...");
            try
            {
                _serverProcess.Kill(entireProcessTree: true);
                await _serverProcess.WaitForExitAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Mem0] 停止服务时出错");
            }
        }
        IsReady = false;
    }

    /// <summary>
    /// 尝试通过 winget 自动安装 Python 3.12
    /// </summary>
    private async Task<bool> TryInstallPythonAsync(CancellationToken ct)
    {
        // 检查 winget 是否可用
        try
        {
            var (wingetCheck, _) = await RunProcessAsync("winget", "--version", ct: ct);
            if (wingetCheck != 0)
            {
                _logger.LogWarning("[Mem0] winget 不可用，无法自动安装 Python");
                ReportProgress("winget 不可用，请手动安装 Python 3.10+: https://www.python.org/downloads/");
                return false;
            }
        }
        catch
        {
            _logger.LogWarning("[Mem0] winget 不可用，无法自动安装 Python");
            ReportProgress("winget 不可用，请手动安装 Python 3.10+: https://www.python.org/downloads/");
            return false;
        }

        ReportProgress("正在通过 winget 安装 Python 3.12 (可能需要几分钟)...");
        try
        {
            var (exitCode, output) = await RunProcessAsync(
                "winget",
                "install Python.Python.3.12 --silent --accept-source-agreements --accept-package-agreements",
                timeoutSeconds: 300,
                ct: ct);

            if (exitCode != 0)
            {
                _logger.LogError("[Mem0] winget 安装 Python 失败 (exit={Code}): {Output}", exitCode, output);
                return false;
            }

            _logger.LogInformation("[Mem0] Python 3.12 已通过 winget 安装");
            ReportProgress("Python 3.12 安装完成，正在刷新环境变量...");

            // winget 安装后 PATH 已更新，但当前进程需要重新读取
            RefreshEnvironmentPath();
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogError("[Mem0] winget 安装 Python 超时");
            ReportProgress("Python 安装超时，请手动安装 Python 3.10+");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Mem0] 自动安装 Python 失败");
            return false;
        }
    }

    /// <summary>
    /// 重新读取系统 PATH 环境变量 — winget 安装后当前进程的 PATH 不会自动刷新
    /// </summary>
    private static void RefreshEnvironmentPath()
    {
        var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        Environment.SetEnvironmentVariable("PATH", $"{userPath};{machinePath}");
    }

    /// <summary>
    /// 查找系统中可用的 Python 3.10+ 路径
    /// </summary>
    private async Task<string?> FindPythonAsync(CancellationToken ct)
    {
        // 按优先级尝试不同的 Python 命令
        string[] candidates = ["python", "python3", "py -3"];

        foreach (var candidate in candidates)
        {
            try
            {
                var (exitCode, output) = await RunProcessAsync(candidate, "--version", ct: ct);
                if (exitCode == 0 && output.Contains("Python 3."))
                {
                    // 解析版本号，确保 >= 3.10
                    var versionStr = output.Replace("Python ", "").Trim();
                    if (Version.TryParse(versionStr, out var version) && version >= new Version(3, 10))
                    {
                        return candidate;
                    }
                    _logger.LogWarning("[Mem0] Python 版本过低: {Version}，需要 3.10+", versionStr);
                }
            }
            catch
            {
                // 该候选不可用，继续尝试
            }
        }

        return null;
    }

    /// <summary>
    /// 确保安装目录结构存在
    /// </summary>
    private void EnsureDirectories()
    {
        var paths = new[]
        {
            _config.InstallPath,
            Path.Combine(_config.InstallPath, "data"),
        };

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logger.LogInformation("[Mem0] 创建目录: {Path}", path);
            }
        }
    }

    /// <summary>
    /// 创建 Python 虚拟环境
    /// </summary>
    private async Task CreateVenvAsync(string pythonPath, CancellationToken ct)
    {
        var venvPath = GetVenvPath();
        var (exitCode, output) = await RunProcessAsync(pythonPath, $"-m venv \"{venvPath}\"", ct: ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"创建虚拟环境失败 (exit={exitCode}): {output}");

        _logger.LogInformation("[Mem0] 虚拟环境已创建: {Path}", venvPath);
    }

    /// <summary>
    /// 检查 mem0ai 是否已安装在虚拟环境中
    /// </summary>
    private async Task<bool> IsMem0InstalledAsync(CancellationToken ct)
    {
        var pip = GetVenvPipPath();
        if (!File.Exists(pip))
            return false;

        try
        {
            var (exitCode, _) = await RunProcessAsync(pip, "show mem0ai", ct: ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在虚拟环境中安装 mem0ai 及 REST 服务依赖
    /// </summary>
    private async Task InstallMem0PackagesAsync(CancellationToken ct)
    {
        var pip = GetVenvPipPath();

        // 先升级 pip
        var venvPython = GetVenvPythonPath();
        await RunProcessAsync(venvPython, "-m pip install --upgrade pip", timeoutSeconds: 120, ct: ct);

        // 安装 mem0ai + fastapi + uvicorn
        var (exitCode, output) = await RunProcessAsync(
            pip,
            "install mem0ai fastapi uvicorn[standard]",
            timeoutSeconds: 300,
            ct: ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"安装 mem0ai 失败 (exit={exitCode}): {output}");

        _logger.LogInformation("[Mem0] mem0ai 及依赖安装完成");
    }

    /// <summary>
    /// 写入 Mem0 REST 服务 Python 脚本
    /// </summary>
    private async Task WriteServerScriptAsync(CancellationToken ct)
    {
        var scriptPath = GetServerScriptPath();
        var dataPath = Path.Combine(_config.InstallPath, "data").Replace("\\", "/");
        var configPath = GetConfigPath().Replace("\\", "/");

        var script = $$""""
            # OwnerAI — Mem0 REST 服务
            # 提供三表任务缓存的向量检索与存储能力
            import json
            import os
            import sys
            import time
            from pathlib import Path

            import uvicorn
            from fastapi import FastAPI, HTTPException
            from pydantic import BaseModel
            from mem0 import Memory

            # ── 配置 ──────────────────────────────────────────────
            DATA_DIR = "{{dataPath}}"
            CONFIG_PATH = "{{configPath}}"

            def load_config():
                if os.path.exists(CONFIG_PATH):
                    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                        return json.load(f)
                return {}

            cfg = load_config()

            # Mem0 配置 — 使用本地 Qdrant 存储
            mem0_config = {
                "version": "v1.1",
                "vector_store": {
                    "provider": "qdrant",
                    "config": {
                        "collection_name": "ownerai_task_cache",
                        "path": os.path.join(DATA_DIR, "qdrant_data"),
                    }
                }
            }

            memory = Memory.from_config(mem0_config)

            app = FastAPI(title="OwnerAI Mem0 Service", version="1.0.0")

            # ── 请求/响应模型 ────────────────────────────────────

            class SearchRequest(BaseModel):
                query: str
                top_k: int = 5
                user_id: str = "owner"

            class StoreRequest(BaseModel):
                content: str
                user_id: str = "owner"
                metadata: dict | None = None

            class DeleteRequest(BaseModel):
                memory_id: str

            # ── API 端点 ─────────────────────────────────────────

            @app.get("/health")
            async def health():
                return {"status": "ok", "service": "mem0", "timestamp": time.time()}

            @app.post("/search")
            async def search(req: SearchRequest):
                try:
                    results = memory.search(req.query, user_id=req.user_id, limit=req.top_k)
                    # 统一返回格式
                    items = []
                    memories = results.get("results", results) if isinstance(results, dict) else results
                    for r in memories:
                        item = {
                            "id": r.get("id", ""),
                            "memory": r.get("memory", ""),
                            "score": r.get("score", 0.0),
                            "metadata": r.get("metadata", {}),
                            "created_at": r.get("created_at", ""),
                        }
                        items.append(item)
                    return {"results": items}
                except Exception as e:
                    raise HTTPException(status_code=500, detail=str(e))

            @app.post("/store")
            async def store(req: StoreRequest):
                try:
                    result = memory.add(
                        req.content,
                        user_id=req.user_id,
                        metadata=req.metadata or {},
                    )
                    return {"result": result}
                except Exception as e:
                    raise HTTPException(status_code=500, detail=str(e))

            @app.delete("/memory/{memory_id}")
            async def delete_memory(memory_id: str):
                try:
                    memory.delete(memory_id)
                    return {"status": "deleted", "id": memory_id}
                except Exception as e:
                    raise HTTPException(status_code=500, detail=str(e))

            @app.get("/memories")
            async def list_memories(user_id: str = "owner"):
                try:
                    result = memory.get_all(user_id=user_id)
                    memories = result.get("results", result) if isinstance(result, dict) else result
                    return {"memories": memories}
                except Exception as e:
                    raise HTTPException(status_code=500, detail=str(e))

            # ── 启动 ─────────────────────────────────────────────

            if __name__ == "__main__":
                host = cfg.get("host", "{{_config.Host}}")
                port = cfg.get("port", {{_config.Port}})
                print(f"[Mem0] Starting server on {host}:{port}")
                print(f"[Mem0] Data directory: {DATA_DIR}")
                uvicorn.run(app, host=host, port=port, log_level="info")
            """";

        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, ct);
        _logger.LogInformation("[Mem0] 服务脚本已写入: {Path}", scriptPath);
    }

    /// <summary>
    /// 写入 Mem0 运行配置
    /// </summary>
    private async Task WriteConfigAsync(CancellationToken ct)
    {
        var configPath = GetConfigPath();

        // 仅在配置不存在时写入
        if (File.Exists(configPath))
            return;

        var config = $$"""
            {
                "host": "{{_config.Host}}",
                "port": {{_config.Port}},
                "data_dir": "{{Path.Combine(_config.InstallPath, "data").Replace("\\", "/")}}"
            }
            """;

        await File.WriteAllTextAsync(configPath, config, Encoding.UTF8, ct);
        _logger.LogInformation("[Mem0] 配置已写入: {Path}", configPath);
    }

    /// <summary>
    /// 启动 Mem0 REST 服务进程
    /// </summary>
    private Task StartServerAsync(CancellationToken ct)
    {
        var venvPython = GetVenvPythonPath();
        var scriptPath = GetServerScriptPath();

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = $"\"{scriptPath}\"",
                WorkingDirectory = _config.InstallPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };

        _serverProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[Mem0:stdout] {Line}", e.Data);
        };

        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[Mem0:stderr] {Line}", e.Data);
        };

        _serverProcess.Exited += (_, _) =>
        {
            _logger.LogWarning("[Mem0] 服务进程已退出 (ExitCode={Code})", _serverProcess.ExitCode);
            IsReady = false;
        };

        if (!_serverProcess.Start())
            throw new InvalidOperationException("启动 Mem0 服务进程失败");

        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        _logger.LogInformation("[Mem0] 服务进程已启动 (PID={Pid})", _serverProcess.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 等待 Mem0 服务健康检查通过
    /// </summary>
    private async Task<bool> WaitForServerReadyAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_config.StartupTimeoutSeconds);
        var interval = TimeSpan.FromMilliseconds(_config.HealthCheckIntervalMs);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            // 检查进程是否意外退出
            if (_serverProcess is { HasExited: true })
            {
                _logger.LogError("[Mem0] 服务进程启动后退出 (ExitCode={Code})", _serverProcess.ExitCode);
                return false;
            }

            if (await IsServerRunningAsync(ct))
                return true;

            await Task.Delay(interval, ct);
        }

        return false;
    }

    /// <summary>
    /// 运行外部进程并等待完成
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"进程 '{fileName} {arguments}' 执行超时 ({timeoutSeconds}s)");
        }

        return (process.ExitCode, output.ToString());
    }

    // ── 路径工具 ────────────────────────────────────────────

    private string GetVenvPath() => Path.Combine(_config.InstallPath, "venv");

    private string GetVenvPythonPath() => Path.Combine(GetVenvPath(), "Scripts", "python.exe");

    private string GetVenvPipPath() => Path.Combine(GetVenvPath(), "Scripts", "pip.exe");

    private string GetServerScriptPath() => Path.Combine(_config.InstallPath, "mem0_server.py");

    private string GetConfigPath() => Path.Combine(_config.InstallPath, "mem0_config.json");

    public void Dispose()
    {
        _httpClient.Dispose();
        if (_serverProcess is { HasExited: false })
        {
            try { _serverProcess.Kill(entireProcessTree: true); } catch { /* 最终清理 */ }
        }
        _serverProcess?.Dispose();
    }
}
