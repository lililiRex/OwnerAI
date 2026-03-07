using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Providers;

/// <summary>
/// 模型供应商故障转移 — 自动切换备用模型
/// </summary>
public sealed class ProviderFailover(
    ProviderRegistry registry,
    IModelMetricsManager metricsManager,
    ILogger<ProviderFailover> logger) : IChatClient
{
    private static readonly AsyncLocal<ModelWorkCategory?> s_currentWorkCategory = new();

    public ChatClientMetadata Metadata { get; } = new("OwnerAI-Failover");

    public static IDisposable BeginWorkCategoryScope(ModelWorkCategory workCategory)
    {
        var previous = s_currentWorkCategory.Value;
        s_currentWorkCategory.Value = workCategory;
        return new WorkCategoryScope(previous);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var providers = GetProvidersForCurrentScope();
        if (providers.Count == 0)
            throw new InvalidOperationException(
                "未配置任何主模型。请在设置中至少添加一个「⭐ 主模型」供应商，保存后重启应用。");

        List<Exception>? exceptions = null;

        foreach (var entry in providers)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await entry.Client.GetResponseAsync(messages, options, cancellationToken);
                sw.Stop();
                _ = RecordMetricAsync(entry.Name, sw.ElapsedMilliseconds, success: true);
                return response;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                sw.Stop();
                _ = RecordMetricAsync(entry.Name, sw.ElapsedMilliseconds, success: false);
                logger.LogWarning(ex, "Provider {Provider} failed, trying next", entry.Name);
                (exceptions ??= []).Add(ex);
            }
        }

        throw new AggregateException("所有主模型均调用失败", exceptions ?? []);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var providers = GetProvidersForCurrentScope();
        if (providers.Count == 0)
            throw new InvalidOperationException(
                "未配置任何主模型。请在设置中至少添加一个「⭐ 主模型」供应商，保存后重启应用。");

        List<Exception>? exceptions = null;

        foreach (var entry in providers)
        {
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            ChatResponseUpdate? firstChunk = null;
            bool probeSucceeded = false;
            var sw = Stopwatch.StartNew();

            // 探测阶段：尝试获取第一个 chunk（认证/网络错误在此抛出）
            try
            {
                enumerator = entry.Client.GetStreamingResponseAsync(
                    messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);

                if (await enumerator.MoveNextAsync())
                {
                    firstChunk = enumerator.Current;
                    probeSucceeded = true;
                }
                else
                {
                    probeSucceeded = true; // 空流也算成功
                    await enumerator.DisposeAsync();
                    enumerator = null;
                }
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                sw.Stop();
                _ = RecordMetricAsync(entry.Name, sw.ElapsedMilliseconds, success: false);
                logger.LogWarning(ex, "Provider {Provider} streaming failed, trying next", entry.Name);
                (exceptions ??= []).Add(ex);
                if (enumerator is not null)
                    await enumerator.DisposeAsync();
                continue;
            }

            // 流式输出阶段：yield 不在 try-catch 内
            if (probeSucceeded)
            {
                if (firstChunk is not null)
                    yield return firstChunk;

                if (enumerator is not null)
                {
                    try
                    {
                        while (await enumerator.MoveNextAsync())
                        {
                            yield return enumerator.Current;
                        }
                    }
                    finally
                    {
                        sw.Stop();
                        _ = RecordMetricAsync(entry.Name, sw.ElapsedMilliseconds, success: true);
                        await enumerator.DisposeAsync();
                    }
                }
                else
                {
                    sw.Stop();
                    _ = RecordMetricAsync(entry.Name, sw.ElapsedMilliseconds, success: true);
                }
                yield break;
            }
        }

        throw new AggregateException("所有主模型流式调用均失败", exceptions ?? []);
    }

    /// <summary>
    /// 判断异常是否可通过切换供应商重试（网络错误、超时、API 认证/限流错误等）
    /// </summary>
    private static bool IsRetryable(Exception ex) =>
        ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException);

    /// <summary>异步记录模型调用度量（fire-and-forget，不阻塞主流程）</summary>
    private async Task RecordMetricAsync(string providerName, long latencyMs, bool success)
    {
        try
        {
            var workCategory = s_currentWorkCategory.Value?.ToString() ?? "General";
            await metricsManager.RecordCallAsync(new ModelCallMetric
            {
                ProviderName = providerName,
                WorkCategory = workCategory,
                LatencyMs = latencyMs,
                Success = success,
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to record model metric for {Provider}", providerName);
        }
    }

    private IReadOnlyList<ProviderEntry> GetProvidersForCurrentScope()
        => s_currentWorkCategory.Value is { } workCategory
            ? registry.GetProvidersForWorkCategory(workCategory)
            : registry.GetPrimaryProviders();

    public void Dispose()
    {
        foreach (var entry in registry.GetAll())
        {
            entry.Client.Dispose();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    private sealed class WorkCategoryScope(ModelWorkCategory? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                s_currentWorkCategory.Value = previous;
        }
    }
}
