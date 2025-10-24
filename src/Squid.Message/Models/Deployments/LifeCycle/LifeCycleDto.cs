namespace Squid.Message.Models.Deployments.LifeCycle;

public class LifeCycleDto : IHasDualRetentionPolicies
{
    public int Id { get; set; }

    public string Name { get; set; }

    public byte[] DataVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public int SpaceId { get; set; }

    public string Slug { get; set; }
    
    public int ReleaseRetentionPolicyId { get; set; }
    
    public int TentacleRetentionPolicyId { get; set; }

    public RetentionPolicyDto ReleaseRetentionPolicy { get; set; } = null;
    
    public RetentionPolicyDto TentacleRetentionPolicy { get; set; } = null;
}