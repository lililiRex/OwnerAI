using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OwnerAI.Configuration;
using OwnerAI.Gateway.Events;
using OwnerAI.Gateway.Health;
using OwnerAI.Gateway.Pipeline;
using OwnerAI.Gateway.Routing;
using OwnerAI.Gateway.Sessions;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Gateway;

/// <summary>
/// Gateway 服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOwnerAIGateway(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 配置
        services.AddOwnerAIConfiguration(configuration);

        // 事件总线
        services.AddSingleton<IEventBus, InMemoryEventBus>();

        // 会话管理
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<ConversationHistory>();

        // 路由
        services.AddSingleton<IMessageRouter, MessageRouter>();

        // 健康监控
        services.AddSingleton<IHealthMonitor, HealthMonitor>();
        services.AddHostedService<HealthCheckRegistrar>();

        // 中间件注册
        services.AddTransient<LoggingMiddleware>();
        services.AddTransient<AuthMiddleware>();
        services.AddTransient<RateLimitMiddleware>();
        services.AddTransient<RoutingMiddleware>();
        services.AddTransient<AgentMiddleware>();
        services.AddTransient<AuditMiddleware>();
        services.AddTransient<ErrorHandlingMiddleware>();

        // 管道构建
        services.AddSingleton(sp =>
        {
            var pipeline = new GatewayPipeline();
            pipeline.Use<ErrorHandlingMiddleware>();
            pipeline.Use<LoggingMiddleware>();
            pipeline.Use<AuthMiddleware>();
            pipeline.Use<RateLimitMiddleware>();
            pipeline.Use<RoutingMiddleware>();
            pipeline.Use<AgentMiddleware>();
            pipeline.Use<AuditMiddleware>();
            return pipeline;
        });

        // Gateway 引擎
        services.AddSingleton<GatewayEngine>();

        return services;
    }
}
