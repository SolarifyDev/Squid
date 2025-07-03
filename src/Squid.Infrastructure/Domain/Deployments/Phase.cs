namespace Squid.Core.Domain.Deployments;

public class Phase : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public Guid LifecycleId { get; set; }
    
    public string Name { get; set; }

    public string AutomaticDeploymentTargets { get; set; }

    public string OptionalDeploymentTargets { get; set; }

    public int MinimumEnvironmentsBeforePromotion { get; set; }

    public bool IsOptionalPhase { get; set; }

    public bool IsPriorityPhase { get; set; }

    public Guid ReleaseRetentionPolicyId { get; set; }

    public Guid TentacleRetentionPolicyId { get; set; }
}