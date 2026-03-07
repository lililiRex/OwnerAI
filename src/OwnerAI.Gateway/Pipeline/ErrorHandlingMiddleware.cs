using Microsoft.Extensions.Logging;
using OwnerAI.Shared;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 异常处理中间件 — 捕获未处理异常，记录日志并向上层传递
/// </summary>
public sealed class ErrorHandlingMiddleware(ILogger<ErrorHandlingMiddleware> logger) : IGatewayMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct)
    {
        try
        {
            await next(context, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[Error] Operation cancelled for session {Session}", context.SessionId);
            context.Response = new ReplyPayload { Text = "操作已取消。" };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Error] Unhandled exception in session {Session}", context.SessionId);

            // 将错误信息写入 Response 并标记为失败，让 UI 层能展示具体原因
            var detail = ex is AggregateException agg && agg.InnerExceptions.Count > 0
                ? agg.InnerExceptions[0].Message
                : ex.Message;

            context.Response = new ReplyPayload
            {
                Text = detail,
                IsError = true,
            };
        }
    }
}
