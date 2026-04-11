using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// A lightweight, serialisation-friendly view of a deployment target carried by
/// <see cref="DeploymentPlan"/>. Decoupled from <c>Machine</c> / <c>DeploymentTargetContext</c>
/// so the Preview UI can consume plans without depending on executor internals.
/// </summary>
public sealed record PlannedTarget
{
    /// <summary>Primary key of the underlying machine.</summary>
    public required int MachineId { get; init; }

    /// <summary>Display name of the machine.</summary>
    public required string MachineName { get; init; }

    /// <summary>The machine's roles, ordered for deterministic rendering.</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>The communication style resolved for this target (never <see cref="CommunicationStyle.Unknown"/> on a healthy target).</summary>
    public CommunicationStyle CommunicationStyle { get; init; } = CommunicationStyle.Unknown;
}
