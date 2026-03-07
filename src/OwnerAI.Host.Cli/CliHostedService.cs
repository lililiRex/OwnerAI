using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwnerAI.Gateway;
using OwnerAI.Shared;

namespace OwnerAI.Host.Cli;

/// <summary>
/// CLI 交互式宿主服务
/// </summary>
public sealed class CliHostedService(
    GatewayEngine gateway,
    ILogger<CliHostedService> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待应用完全启动
        await Task.Yield();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintBanner();

        while (!stoppingToken.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("\n你: ");
            Console.ResetColor();

            string? input;
            try
            {
                input = await Task.Run(() => Console.ReadLine(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // 退出命令
            if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("👋 再见！");
                lifetime.StopApplication();
                break;
            }

            // 帮助命令
            if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                continue;
            }

            // 状态命令
            if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                PrintStatus();
                continue;
            }

            // 发送消息到 Gateway
            try
            {
                var message = new InboundMessage
                {
                    Id = Ulid.NewUlid().ToString(),
                    ChannelId = "cli",
                    SenderId = "owner",
                    SenderName = Environment.UserName,
                    Text = input,
                    Timestamp = DateTimeOffset.Now,
                };

                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("\nAI: ");
                Console.ResetColor();

                var reply = await gateway.ProcessMessageAsync(message, ct: stoppingToken);

                if (reply is not null)
                {
                    Console.WriteLine(reply.Text);

                    if (reply.ToolCalls is { Count: > 0 })
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        foreach (var call in reply.ToolCalls)
                        {
                            Console.WriteLine($"  🔧 {call.ToolName} ({call.Duration.TotalMilliseconds:F0}ms) → {(call.Success ? "✅" : "❌")}");
                        }
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine("(无回复)");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing message");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ 错误: {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine();
        Console.WriteLine("   OwnerAI — 你的个人 AI 桌面助手 (CLI 模式)");
        Console.WriteLine("   版本: 0.1.0 | .NET 10 | Phase 1");
        Console.WriteLine();
        Console.WriteLine("   输入 /help 查看可用命令");
        Console.ResetColor();
    }

    private static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("""

        可用命令:
          /help    — 显示此帮助信息
          /status  — 查看系统状态
          /quit    — 退出程序
          /exit    — 退出程序

        直接输入文字与 AI 对话。
        """);
        Console.ResetColor();
    }

    private static void PrintStatus()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"""

        系统状态:
          运行时间: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\.hh\:mm\:ss}
          进程内存: {GC.GetTotalMemory(false) / (1024.0 * 1024):F1} MB
          GC Gen0/1/2: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}
          线程池: {ThreadPool.ThreadCount} 线程
        """);
        Console.ResetColor();
    }
}
