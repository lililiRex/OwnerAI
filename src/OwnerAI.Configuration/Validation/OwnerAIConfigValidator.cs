using FluentValidation;

namespace OwnerAI.Configuration.Validation;

/// <summary>
/// OwnerAI 根配置校验器
/// </summary>
public sealed class OwnerAIConfigValidator : AbstractValidator<OwnerAIConfig>
{
    public OwnerAIConfigValidator()
    {
        RuleFor(x => x.Agent).NotNull().SetValidator(new AgentConfigValidator());
        RuleFor(x => x.Security).NotNull().SetValidator(new SecurityConfigValidator());
        RuleFor(x => x.Memory).NotNull().SetValidator(new MemoryConfigValidator());
    }
}

public sealed class AgentConfigValidator : AbstractValidator<AgentConfig>
{
    public AgentConfigValidator()
    {
        RuleFor(x => x.DefaultModel).NotEmpty().WithMessage("默认模型不能为空");
        RuleFor(x => x.Temperature).InclusiveBetween(0f, 2f).WithMessage("温度必须在 0-2 之间");
        RuleFor(x => x.MaxToolIterations).InclusiveBetween(1, 100).WithMessage("最大工具迭代次数必须在 1-100 之间");
        RuleFor(x => x.ContextWindowTokenBudget).GreaterThan(0).WithMessage("上下文窗口预算必须大于 0");
        RuleFor(x => x.Persona).NotEmpty().WithMessage("Persona 不能为空");
    }
}

public sealed class SecurityConfigValidator : AbstractValidator<SecurityConfig>
{
    public SecurityConfigValidator()
    {
        RuleFor(x => x.AuditRetentionDays).GreaterThan(0).WithMessage("审计日志保留天数必须大于 0");
    }
}

public sealed class MemoryConfigValidator : AbstractValidator<MemoryConfig>
{
    public MemoryConfigValidator()
    {
        RuleFor(x => x.EmbeddingDimension).GreaterThan(0).WithMessage("嵌入维度必须大于 0");
        RuleFor(x => x.FragmentWindowSize).GreaterThan(0).WithMessage("碎片窗口大小必须大于 0");
        RuleFor(x => x.FragmentMergeWeight).InclusiveBetween(0f, 1f).WithMessage("碎片融合权重必须在 0-1 之间");
        RuleFor(x => x.DefaultTopK).GreaterThan(0).WithMessage("默认 TopK 必须大于 0");
        RuleFor(x => x.DatabasePath).NotEmpty().WithMessage("数据库路径不能为空");
    }
}
