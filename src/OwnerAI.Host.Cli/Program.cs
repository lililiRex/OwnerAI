using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OwnerAI.Agent;
using OwnerAI.Agent.Tools;
using OwnerAI.Gateway;
using OwnerAI.Host.Cli;
using OwnerAI.Security;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "ownerai-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    Log.Information("OwnerAI CLI starting...");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog();

    // 注册核心服务
    builder.Services.AddOwnerAIGateway(builder.Configuration);
    builder.Services.AddOwnerAIAgent(builder.Configuration);
    builder.Services.AddOwnerAITools();
    builder.Services.AddOwnerAISecurity(builder.Configuration);

    // 注册 CLI 交互服务
    builder.Services.AddHostedService<CliHostedService>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "OwnerAI CLI terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
