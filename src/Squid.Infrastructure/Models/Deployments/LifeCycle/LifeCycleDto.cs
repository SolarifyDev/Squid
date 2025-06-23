namespace Squid.Core.Models.Deployments.LifeCycle;

public class LifeCycleDto
{
    public Guid Id { get; set; }

    public string Name { get; set; }

    public byte[] DataVersion { get; set; }

    public Guid SpaceId { get; set; }

    public string Slug { get; set; }
    
    public Guid ReleaseRetentionPolicyId { get; set; }
    
    public Guid TentacleRetentionPolicyId { get; set; }

    public RetentionPolicyDto ReleaseRetentionPolicy { get; set; } = null;
    
    public RetentionPolicyDto TentacleRetentionPolicy { get; set; } = null;
}