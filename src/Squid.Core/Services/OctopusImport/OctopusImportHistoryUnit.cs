using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// One Octopus deployment and its server task, written together. The ids are already-mapped Squid ids.
/// <see cref="ExistingDeploymentId"/> / <see cref="ExistingTaskId"/> are the rows an earlier import wrote
/// (null for a create; a null task id under an existing deployment re-creates the task).
/// </summary>
public sealed record OctopusImportHistoryUnit
{
    public int SpaceId { get; init; }
    public int ProjectId { get; init; }
    public int ChannelId { get; init; }
    public int ReleaseId { get; init; }
    public int EnvironmentId { get; init; }

    public int? ExistingDeploymentId { get; init; }
    public int? ExistingTaskId { get; init; }

    public OctopusDeploymentDto Deployment { get; init; }
    public OctopusServerTaskDto Task { get; init; }
    public OctopusTaskHistoryDetails Details { get; init; }

    public bool ScalarChanged { get; init; } = true;
    public bool ReplaceChildren { get; init; } = true;

    /// <summary>Octopus team id → Squid team id, or null when the team was not imported.</summary>
    public Func<string, int?> TeamResolver { get; init; }

    /// <summary>Octopus machine id → name, for the raw machine ids the deployment keeps (machines are never imported).</summary>
    public IReadOnlyDictionary<string, string> MachineNames { get; init; }
}
