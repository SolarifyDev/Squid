namespace Squid.Message.Domain.Deployments;

public class Phase : IEntity<Guid>
{
    public Guid Id { get; set; }
    
    public Guid LifecycleId { get; set; }
    
    public string Name { get; set; }

    public string AutomaticDeploymentTargets { get; set; } = string.Empty;

    public string OptionalDeploymentTargets { get; set; } = string.Empty;

    public int MinimumEnvironmentsBeforePromotion { get; set; }

    public bool IsOptionalPhase { get; set; }

    public bool IsPriorityPhase { get; set; }

    public Guid ReleaseRetentionPolicyId { get; set; }

    public Guid TentacleRetentionPolicyId { get; set; }
}