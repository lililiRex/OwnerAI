using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwnerAI.Gateway.Health;

namespace OwnerAI.Gateway;

/// <summary>
/// 健康检查注册器 — 在启动时注册所有子系统健康检查
/// </summary>
public sealed class HealthCheckRegistrar(
    IHealthMonitor healthMonitor,
    ILogger<HealthCheckRegistrar> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // SQLite 连接检查
        healthMonitor.RegisterCheck("sqlite", async token =>
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OwnerAI", "memory.db");

            if (!File.Exists(dbPath))
                return false;

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(token);
            return conn.State == System.Data.ConnectionState.Open;
        });

        // LLM 端点可达性检查
        healthMonitor.RegisterCheck("llm_endpoint", async token =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                // Ollama 默认端点
                var response = await http.GetAsync("http://localhost:11434/api/tags", token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        });

        // Docker 可用性检查
        healthMonitor.RegisterCheck("docker", async token =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "version --format json",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) return false;
                await proc.WaitForExitAsync(token);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });

        // 磁盘空间检查
        healthMonitor.RegisterCheck("disk_space", token =>
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:");
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            return Task.FromResult(freeGb > 1.0);
        });

        logger.LogInformation("[HealthCheckRegistrar] Registered 4 health checks");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
