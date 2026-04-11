namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// A structured reason why a <see cref="DeploymentPlan"/> cannot be executed. Collected
/// into <see cref="DeploymentPlan.BlockingReasons"/> and surfaced in Preview UIs and
/// <see cref="Exceptions.DeploymentPlanValidationException.Reasons"/>.
/// </summary>
public sealed record PlanBlockingReason
{
    /// <summary>Stable short code (see <see cref="PlanBlockingReasonCodes"/>).</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable message suitable for logs and UI tooltips.</summary>
    public required string Message { get; init; }

    /// <summary>Optional step the blocker is tied to (0 = not step-specific).</summary>
    public int StepId { get; init; }

    /// <summary>Step name for context-free display.</summary>
    public string StepName { get; init; } = string.Empty;

    /// <summary>Optional machine the blocker is tied to (0 = not machine-specific).</summary>
    public int MachineId { get; init; }

    /// <summary>Machine name for context-free display.</summary>
    public string MachineName { get; init; } = string.Empty;

    /// <summary>Optional sub-code or detail (e.g. the specific capability violation code).</summary>
    public string? Detail { get; init; }
}
