namespace OwnerAI.Host.Desktop.Models;

public sealed class EvolutionPlanStepViewItem
{
    public required string StepId { get; init; }
    public required string Title { get; init; }
    public required string StepTypeText { get; init; }
    public required string StatusText { get; init; }
    public required string TitleText { get; init; }
    public string? Result { get; init; }
}

public sealed class EvolutionPlannedGapViewItem
{
    public required string GapId { get; init; }
    public required string Description { get; init; }
    public required string CategoryText { get; init; }
    public required string StatusText { get; init; }
    public required string ProgressText { get; init; }
    public string? LastAttemptLog { get; init; }
    public required IReadOnlyList<EvolutionPlanStepViewItem> Steps { get; init; }
}
