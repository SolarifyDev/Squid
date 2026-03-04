using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments.LifeCycle;

public class LifecyclePhaseDto
{
    public int Id { get; set; }

    public int LifecycleId { get; set; }

    public string Name { get; set; }

    public int SortOrder { get; set; }

    public List<int> AutomaticDeploymentTargetIds { get; set; } = new();

    public List<int> OptionalDeploymentTargetIds { get; set; } = new();

    public int MinimumEnvironmentsBeforePromotion { get; set; }

    public bool IsOptionalPhase { get; set; }

    public bool IsPriorityPhase { get; set; }

    // Release retention (nullable = inherit from lifecycle)
    public RetentionPolicyUnit? ReleaseRetentionUnit { get; set; }
    public int? ReleaseRetentionQuantity { get; set; }
    public bool? ReleaseRetentionKeepForever { get; set; }

    // Tentacle retention (nullable = inherit from lifecycle)
    public RetentionPolicyUnit? TentacleRetentionUnit { get; set; }
    public int? TentacleRetentionQuantity { get; set; }
    public bool? TentacleRetentionKeepForever { get; set; }
}
