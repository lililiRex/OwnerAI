using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Xaml.Controls;
using OwnerAI.Configuration;
using OwnerAI.Security.Approval;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 桌面审批服务 — 使用 WinUI 3 ContentDialog 替代控制台交互
/// </summary>
public sealed class DesktopApprovalService : ApprovalService
{
    private readonly IOptions<OwnerAIConfig> _config;
    private readonly ILogger _logger;

    public DesktopApprovalService(
        IOptions<OwnerAIConfig> config,
        ILogger<DesktopApprovalService> logger) : base(config, logger)
    {
        _config = config;
        _logger = logger;
    }

    public override async Task<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var policy = _config.Value.Security.DefaultApprovalPolicy;

        // 自动批准策略
        if (policy == ApprovalPolicy.AutoApprove)
        {
            _logger.LogInformation("[DesktopApproval] Auto-approved: {Operation}", request.Operation);
            return ApprovalResult.Allow("自动批准策略");
        }

        // 仅高风险需要审批 → 低级别自动通过
        if (policy == ApprovalPolicy.HighRiskOnly && request.Level == ApprovalLevel.Low)
        {
            _logger.LogInformation("[DesktopApproval] Auto-approved (low risk): {Operation}", request.Operation);
            return ApprovalResult.Allow("低风险自动通过");
        }

        // 需要用户审批 — 通过 ContentDialog
        try
        {
            var tcs = new TaskCompletionSource<ApprovalResult>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(request.Timeout);

            App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var levelEmoji = request.Level switch
                    {
                        ApprovalLevel.Critical => "🔴",
                        ApprovalLevel.High => "🟠",
                        ApprovalLevel.Medium => "🟡",
                        _ => "🔵",
                    };

                    var content = $"{levelEmoji} {request.Operation}";
                    if (!string.IsNullOrWhiteSpace(request.Details))
                        content += $"\n\n详情: {request.Details}";
                    content += $"\n\n安全级别: {request.Level}";

                    var dialog = new ContentDialog
                    {
                        Title = "⚠️ 操作审批",
                        Content = content,
                        PrimaryButtonText = "批准",
                        CloseButtonText = "拒绝",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = App.MainWindow?.Content?.XamlRoot,
                    };

                    var result = await dialog.ShowAsync();
                    tcs.TrySetResult(result == ContentDialogResult.Primary
                        ? ApprovalResult.Allow("用户批准")
                        : ApprovalResult.Deny("用户拒绝"));
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(ApprovalResult.Deny($"对话框异常: {ex.Message}"));
                }
            });

            using var reg = cts.Token.Register(() => tcs.TrySetResult(ApprovalResult.Deny("审批超时")));
            var approval = await tcs.Task;

            _logger.LogInformation("[DesktopApproval] {Result}: {Operation}",
                approval.Approved ? "Approved" : "Denied", request.Operation);
            return approval;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DesktopApproval] Failed for {Operation}", request.Operation);
            return ApprovalResult.Deny($"审批异常: {ex.Message}");
        }
    }
}
