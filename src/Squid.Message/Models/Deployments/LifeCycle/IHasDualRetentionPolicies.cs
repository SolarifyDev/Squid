namespace Squid.Message.Models.Deployments.LifeCycle;

public interface IHasDualRetentionPolicies
{
    int ReleaseRetentionPolicyId { get; set; }
    
    int TentacleRetentionPolicyId { get; set; }
    
    RetentionPolicyDto ReleaseRetentionPolicy { get; set; }
    
    RetentionPolicyDto TentacleRetentionPolicy { get; set; }
}