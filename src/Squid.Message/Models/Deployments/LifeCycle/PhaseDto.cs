namespace Squid.Message.Models.Deployments.LifeCycle;

public class PhaseDto
{
    public Guid Id { get; set; }
    
    public Guid LifecycleId { get; set; }
    
    public string Name { get; set; }
    
    public List<string> AutomaticDeploymentTargets { get; set; }
    
    public List<string> OptionalDeploymentTargets { get; set; }
    
    public int MinimumEnvironmentsBeforePromotion { get; set; }
    
    public bool IsOptionalPhase { get; set; }
    
    public bool IsPriorityPhase { get; set; }
    
    public Guid ReleaseRetentionPolicyId { get; set; }
    
    public Guid TentacleRetentionPolicyId { get; set; }

    public RetentionPolicyDto ReleaseRetentionPolicy { get; set; } = null;
    
    public RetentionPolicyDto TentacleRetentionPolicy { get; set; } = null;
}