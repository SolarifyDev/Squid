namespace Squid.Message.Models.Deployments.LifeCycle;

public class LifeCycleDto : IHasDualRetentionPolicies
{
    public Guid Id { get; set; }

    public string Name { get; set; }

    public byte[] DataVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public Guid SpaceId { get; set; }

    public string Slug { get; set; }
    
    public Guid ReleaseRetentionPolicyId { get; set; }
    
    public Guid TentacleRetentionPolicyId { get; set; }

    public RetentionPolicyDto ReleaseRetentionPolicy { get; set; } = null;
    
    public RetentionPolicyDto TentacleRetentionPolicy { get; set; } = null;
}