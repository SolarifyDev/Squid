using Squid.Core.Services.DeploymentExecution.Validation;

namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// The outcome of <c>ICapabilityValidator.Validate</c> for a single
/// <see cref="PlannedTargetDispatch"/>. An empty <see cref="Violations"/> list means the
/// intent is fully supported by the target's transport.
/// </summary>
public sealed record CapabilityValidationResult
{
    /// <summary>Every violation reported by the validator; empty when the dispatch is supported.</summary>
    public IReadOnlyList<CapabilityViolation> Violations { get; init; } = Array.Empty<CapabilityViolation>();

    /// <summary><c>true</c> when <see cref="Violations"/> is empty.</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>A fully-supported result — shared singleton to avoid allocations in the common case.</summary>
    public static readonly CapabilityValidationResult Supported = new();
}
