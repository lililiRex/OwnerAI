using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Security.Approval;

/// <summary>
/// 审批服务实现 — CLI 模式下使用控制台交互，桌面模式下由 UI 层覆盖
/// </summary>
public class ApprovalService(
    IOptions<OwnerAIConfig> config,
    ILogger<ApprovalService> logger) : IApprovalService
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();

    public virtual async Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var policy = config.Value.Security.DefaultApprovalPolicy;

        // 自动批准策略
        if (policy == ApprovalPolicy.AutoApprove)
        {
            logger.LogInformation("[Approval] Auto-approved: {Operation}", request.Operation);
            return ApprovalResult.Allow("自动批准策略");
        }

        // 仅高风险需要审批 → 低级别自动通过
        if (policy == ApprovalPolicy.HighRiskOnly && request.Level == ApprovalLevel.Low)
        {
            logger.LogInformation("[Approval] Auto-approved (low risk): {Operation}", request.Operation);
            return ApprovalResult.Allow("低风险自动通过");
        }

        // 需要用户审批
        var pending = new PendingApproval
        {
            Id = Ulid.NewUlid().ToString(),
            Request = request,
        };

        _pending[pending.Id] = pending;
        logger.LogWarning("[Approval] Waiting for approval: {Operation} ({Level})",
            request.Operation, request.Level);

        // CLI 模式: 控制台交互
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n⚠️  需要审批: {request.Operation}");
        if (request.Details is not null)
            Console.WriteLine($"   详情: {request.Details}");
        Console.WriteLine($"   级别: {request.Level}");
        Console.Write("   是否批准? (y/n): ");
        Console.ResetColor();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(request.Timeout);

            var input = await Task.Run(() => Console.ReadLine(), cts.Token);
            _pending.TryRemove(pending.Id, out _);

            if (input?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
            {
                logger.LogInformation("[Approval] Approved: {Operation}", request.Operation);
                return ApprovalResult.Allow("用户批准");
            }

            logger.LogInformation("[Approval] Denied: {Operation}", request.Operation);
            return ApprovalResult.Deny("用户拒绝");
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(pending.Id, out _);
            logger.LogWarning("[Approval] Timed out: {Operation}", request.Operation);
            return ApprovalResult.Deny("审批超时");
        }
    }

    public IReadOnlyList<PendingApproval> GetPending()
        => [.. _pending.Values];
}
