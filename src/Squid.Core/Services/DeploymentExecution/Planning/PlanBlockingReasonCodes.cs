namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// Stable short strings identifying the kind of blocker that prevents an
/// <see cref="IDeploymentPlanner"/> from producing a runnable plan. Tests match on these
/// codes so the messages can change without breaking assertions.
/// </summary>
public static class PlanBlockingReasonCodes
{
    /// <summary>No target in the candidate pool matches any step's required roles.</summary>
    public const string NoMatchingTargets = "NO_MATCHING_TARGETS";

    /// <summary>The deployment has at least one target-level step but machine selection yielded no machines.</summary>
    public const string NoSelectedMachines = "NO_SELECTED_MACHINES";

    /// <summary>
    /// A capability violation was reported by <c>ICapabilityValidator</c> against a specific
    /// target/intent pair. <see cref="PlanBlockingReason.Detail"/> carries the violation code.
    /// </summary>
    public const string CapabilityViolation = "CAPABILITY_VIOLATION";

    /// <summary>The planner could not resolve any transport for a target (misconfigured endpoint).</summary>
    public const string TransportUnresolved = "TRANSPORT_UNRESOLVED";

    /// <summary>The planner could not resolve an <c>IIntentRenderer</c> for the target's transport in Execute mode.</summary>
    public const string RendererUnresolved = "RENDERER_UNRESOLVED";
}
