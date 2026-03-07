using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OwnerAI.Configuration;

/// <summary>
/// 配置服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOwnerAIConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<OwnerAIConfig>(configuration.GetSection(OwnerAIConfig.SectionName));

        services.AddValidatorsFromAssemblyContaining<OwnerAIConfig>(ServiceLifetime.Singleton);

        return services;
    }
}
