namespace Squid.Message.Models.Deployments.LifeCycle;

public interface IHasDualRetentionPolicies
{
    Guid ReleaseRetentionPolicyId { get; set; }
    
    Guid TentacleRetentionPolicyId { get; set; }
    
    RetentionPolicyDto ReleaseRetentionPolicy { get; set; }
    
    RetentionPolicyDto TentacleRetentionPolicy { get; set; }
}