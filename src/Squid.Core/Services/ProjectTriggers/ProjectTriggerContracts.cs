using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers;

public sealed record ProjectTriggerDefinition(
    int ProjectId, int SpaceId, string TriggerType, string ActionType, string ConfigurationJson);

/// <summary>Configuration references that determine which project and environment permissions are required.</summary>
public sealed record TriggerResourceScope(
    int? ChannelId = null, int? SourceEnvironmentId = null, int? DestinationEnvironmentId = null);

public sealed record TriggerObservation(string SourceKey, string EvidenceJson, DateTimeOffset ObservedAt);

/// <summary>One stable source event resolves to an action or a skip reason visible to project viewers. Keep credentials and raw provider errors out of the reason and evidence.</summary>
public sealed record TriggerOccurrence(
    string OccurrenceKey, string EvidenceJson, TriggerActionPlan Action = null, string SkipReason = null);

/// <summary>Summary is shown to project viewers. Return a short operational outcome without credentials or raw exception text.</summary>
public sealed record TriggerEvaluation(
    string NextCursor, IReadOnlyList<TriggerOccurrence> Occurrences, string Summary, DateTimeOffset? NextDueAt = null);

/// <summary>Persisted action input. Store immutable identifiers and credential references only.</summary>
public sealed class TriggerActionPlan
{
    public int? ChannelId { get; set; }
    public int? SourceEnvironmentId { get; set; }
    public int? EnvironmentId { get; set; }
    public int? ReleaseId { get; set; }
    public int? BaselineDeploymentId { get; set; }
    public string Version { get; set; }
    public string PackageReferenceFingerprint { get; set; }
    public List<CreateReleaseSelectedPackageDto> SelectedPackages { get; set; } = new();
    public List<int> SpecificMachineIds { get; set; } = new();
    public List<int> SkipActionIds { get; set; } = new();
    public bool UseGuidedFailure { get; set; }
}

public sealed record TriggerActionResult(int ReleaseId, int? DeploymentId = null, int? TaskId = null);

public interface IProjectTriggerHandler : IScopedDependency
{
    string TriggerType { get; }
    IReadOnlyList<string> ActionTypes { get; }

    Task<TriggerResourceScope> ValidateAsync(ProjectTriggerDefinition definition, CancellationToken ct);

    Task<TriggerEvaluation> EvaluateAsync(
        ProjectTriggerDefinition definition, TriggerObservation observation, string cursor, CancellationToken ct);
}

public interface IProjectTriggerRegistry : IScopedDependency
{
    IReadOnlyList<ProjectTriggerCapability> Capabilities { get; }

    IProjectTriggerHandler Resolve(string triggerType, string actionType);
}

public interface ITriggerBusinessActions : IScopedDependency
{
    Task<TriggerActionResult> ExecuteAsync(
        ProjectTriggerDefinition definition, string actionType, TriggerActionPlan plan, CancellationToken ct);
}
