using Squid.Message.Enums.Deployments;

namespace Squid.Core.Persistence.Entities.Deployments;

public class LifecyclePhase : IEntity<int>
{
    public int Id { get; set; }

    public int LifecycleId { get; set; }

    public string Name { get; set; }

    public int SortOrder { get; set; }

    public int MinimumEnvironmentsBeforePromotion { get; set; }

    public bool IsOptionalPhase { get; set; }

    public bool IsPriorityPhase { get; set; }

    // Release retention (nullable = inherit from lifecycle)
    public int? ReleaseRetentionQuantity { get; set; }
    public bool? ReleaseRetentionKeepForever { get; set; }
    public RetentionPolicyUnit? ReleaseRetentionUnit { get; set; }

    
    // Tentacle retention (nullable = inherit from lifecycle)
    public int? TentacleRetentionQuantity { get; set; }
    public bool? TentacleRetentionKeepForever { get; set; }
    public RetentionPolicyUnit? TentacleRetentionUnit { get; set; }
}
