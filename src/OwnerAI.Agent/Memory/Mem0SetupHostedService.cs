using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;

namespace OwnerAI.Agent.Memory;

/// <summary>
/// Mem0 启动服务 — 在程序初始化阶段检查并安装/启动 Mem0 向量数据库
/// <para>初始化在后台执行，不阻塞程序启动和 UI 显示。</para>
/// </summary>
public sealed class Mem0SetupHostedService(
    Mem0ServerManager serverManager,
    IOptions<Mem0Config> config,
    ILogger<Mem0SetupHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!config.Value.AutoStart)
        {
            logger.LogInformation("[Mem0Setup] AutoStart 已禁用，跳过 Mem0 初始化");
            return Task.CompletedTask;
        }

        // 在后台执行初始化，不阻塞 UI 显示
        _ = InitializeInBackgroundAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task InitializeInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await serverManager.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            // 初始化失败不阻塞程序运行 — 回退到内存模式
            logger.LogError(ex, "[Mem0Setup] Mem0 初始化失败，任务缓存将使用内存模式。" +
                "可稍后手动安装: pip install mem0ai fastapi uvicorn[standard]");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[Mem0Setup] 正在停止 Mem0 服务...");

        try
        {
            await serverManager.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Mem0Setup] 停止 Mem0 服务时出错");
        }
    }
}
