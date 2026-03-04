using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments.LifeCycle;

public class LifecyclePhaseModel
{
    public string Name { get; set; }
    public int SortOrder { get; set; }
    public bool IsOptionalPhase { get; set; }
    public bool IsPriorityPhase { get; set; }
    public int MinimumEnvironmentsBeforePromotion { get; set; }
    public List<int> AutomaticDeploymentTargetIds { get; set; } = new();
    public List<int> OptionalDeploymentTargetIds { get; set; } = new();
    public RetentionPolicyUnit? ReleaseRetentionUnit { get; set; }
    public int? ReleaseRetentionQuantity { get; set; }
    public bool? ReleaseRetentionKeepForever { get; set; }
    public RetentionPolicyUnit? TentacleRetentionUnit { get; set; }
    public int? TentacleRetentionQuantity { get; set; }
    public bool? TentacleRetentionKeepForever { get; set; }
}
