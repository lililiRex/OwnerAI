using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Hooks;

/// <summary>
/// 钩子管理器 — 收集所有 IHook 实现，按 Priority 排序分发事件
/// </summary>
public sealed class HookManager(
    IEnumerable<IHook> hooks,
    ILogger<HookManager> logger)
{
    private readonly ILookup<string, IHook> _hooksByEvent = hooks
        .OrderBy(h => h.Priority)
        .ToLookup(h => h.EventName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 分发钩子事件 — 按优先级顺序执行，任一钩子返回 Cancel 则中止后续钩子
    /// </summary>
    public async ValueTask<HookResult> DispatchAsync(string eventName, HookContext context, CancellationToken ct)
    {
        if (!_hooksByEvent.Contains(eventName))
            return HookResult.Ok();

        foreach (var hook in _hooksByEvent[eventName])
        {
            try
            {
                var result = await hook.ExecuteAsync(context, ct);

                if (result.ModifiedProperties is not null)
                {
                    foreach (var (key, value) in result.ModifiedProperties)
                        context.Properties[key] = value;
                }

                if (!result.Continue)
                {
                    logger.LogInformation("[HookManager] Event '{Event}' cancelled by hook (Priority={Priority})",
                        eventName, hook.Priority);
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[HookManager] Hook failed for event '{Event}' (Priority={Priority})",
                    eventName, hook.Priority);
            }
        }

        return HookResult.Ok();
    }

    /// <summary>获取已注册的钩子数量</summary>
    public int Count => _hooksByEvent.Sum(g => g.Count());

    /// <summary>获取已注册的事件名称列表</summary>
    public IEnumerable<string> RegisteredEvents => _hooksByEvent.Select(g => g.Key);
}
