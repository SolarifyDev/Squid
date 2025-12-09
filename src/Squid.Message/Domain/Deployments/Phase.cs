namespace Squid.Message.Domain.Deployments;

public class Phase : IEntity<int>
{
    public int Id { get; set; }
    
    public int LifecycleId { get; set; }
    
    public string Name { get; set; }

    public string AutomaticDeploymentTargets { get; set; } = string.Empty;

    public string OptionalDeploymentTargets { get; set; } = string.Empty;

    public int MinimumEnvironmentsBeforePromotion { get; set; }

    public bool IsOptionalPhase { get; set; }

    public bool IsPriorityPhase { get; set; }

    public int ReleaseRetentionPolicyId { get; set; }

    public int TentacleRetentionPolicyId { get; set; }
}