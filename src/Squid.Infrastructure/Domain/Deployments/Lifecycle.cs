namespace Squid.Core.Domain.Deployments;

public class Lifecycle : IEntity<int>
{
    public int Id { get; set; }

    public string Name { get; set; }

    public byte[] DataVersion { get; set; }

    public Guid SpaceId { get; set; }

    public string Slug { get; set; }
    
    public Guid ReleaseRetentionPolicyId { get; set; }
    
    public Guid TentacleRetentionPolicyId { get; set; }
}
