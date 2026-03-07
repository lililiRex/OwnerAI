using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OwnerAI.Security.Approval;
using OwnerAI.Security.Audit;
using OwnerAI.Security.Secrets;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Security;

/// <summary>
/// 安全模块服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOwnerAISecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 审批服务
        services.AddSingleton<IApprovalService, ApprovalService>();

        // 审计日志
        services.AddSingleton<IAuditLogger, AuditLogger>();

        // 秘钥存储
        services.AddSingleton<ISecretStore, DpapiSecretStore>();

        return services;
    }
}
