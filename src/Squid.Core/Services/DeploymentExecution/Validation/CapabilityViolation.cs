using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// A single capability violation produced by <see cref="ICapabilityValidator"/> when an
/// <see cref="Intents.ExecutionIntent"/> cannot be satisfied by a target's
/// <see cref="Transport.ITransportCapabilities"/>.
///
/// <para>
/// Violations are returned as a list rather than thrown, so the preview UI (Phase 6) can
/// surface every reason a step is blocked on a given target at once. The executor wraps
/// any non-empty violation list in a <c>DeploymentPlanValidationException</c>.
/// </para>
/// </summary>
public sealed record CapabilityViolation
{
    /// <summary>Stable code identifying the violation kind. See <see cref="ViolationCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable explanation suitable for logs and preview UI.</summary>
    public required string Message { get; init; }

    /// <summary>The transport the violation was detected against. Defaults to <see cref="CommunicationStyle.Unknown"/>.</summary>
    public CommunicationStyle CommunicationStyle { get; init; } = CommunicationStyle.Unknown;

    /// <summary>Semantic name of the intent that produced the violation.</summary>
    public string IntentName { get; init; } = string.Empty;

    /// <summary>Name of the deployment step that produced the intent, if known.</summary>
    public string StepName { get; init; } = string.Empty;

    /// <summary>Name of the deployment action that produced the intent, if known.</summary>
    public string ActionName { get; init; } = string.Empty;

    /// <summary>Optional subject the code refers to (e.g. the offending syntax name, feature key, or nested path).</summary>
    public string? Detail { get; init; }
}
