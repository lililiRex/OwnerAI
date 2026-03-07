using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OwnerAI.Gateway.Health;

/// <summary>
/// 健康监控实现
/// </summary>
public sealed class HealthMonitor(ILogger<HealthMonitor> logger) : IHealthMonitor
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<bool>>> _checks = new();

    public void RegisterCheck(string name, Func<CancellationToken, Task<bool>> check)
    {
        _checks[name] = check;
    }

    public async Task<IReadOnlyList<SubsystemHealth>> CheckAllAsync(CancellationToken ct)
    {
        var results = new List<SubsystemHealth>();

        foreach (var (name, check) in _checks)
        {
            try
            {
                var healthy = await check(ct);
                results.Add(new SubsystemHealth
                {
                    Name = name,
                    IsHealthy = healthy,
                    Details = healthy ? "OK" : "Unhealthy",
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Health] Check failed for {Name}", name);
                results.Add(new SubsystemHealth
                {
                    Name = name,
                    IsHealthy = false,
                    Details = ex.Message,
                });
            }
        }

        return results;
    }
}
