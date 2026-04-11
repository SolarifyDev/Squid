namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// A single action inside a <see cref="PlannedStep"/>. One action fans out into multiple
/// <see cref="PlannedTargetDispatch"/> records — one per target the step matches. A
/// step-level action (e.g. Manual Intervention) has an empty <see cref="Dispatches"/>
/// list because it does not iterate targets.
/// </summary>
public sealed record PlannedAction
{
    /// <summary>Primary key of the underlying action.</summary>
    public required int ActionId { get; init; }

    /// <summary>Display name of the action.</summary>
    public required string ActionName { get; init; }

    /// <summary>The legacy <c>ActionType</c> string (e.g. <c>Squid.Script</c>).</summary>
    public required string ActionType { get; init; }

    /// <summary>Original ordering inside the parent step.</summary>
    public int ActionOrder { get; init; }

    /// <summary><c>true</c> when the action has <c>ExecutionScope.StepLevel</c>.</summary>
    public bool IsStepLevel { get; init; }

    /// <summary>One dispatch per matched target. Empty for step-level or server-only actions.</summary>
    public IReadOnlyList<PlannedTargetDispatch> Dispatches { get; init; } = Array.Empty<PlannedTargetDispatch>();
}
