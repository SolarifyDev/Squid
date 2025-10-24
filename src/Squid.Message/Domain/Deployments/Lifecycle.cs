namespace Squid.Message.Domain.Deployments;

public class Lifecycle : IEntity<int>
{
    public int Id { get; set; }

    public string Name { get; set; }

    public byte[] DataVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public Guid SpaceId { get; set; }

    public string Slug { get; set; }
    
    public Guid ReleaseRetentionPolicyId { get; set; }
    
    public Guid TentacleRetentionPolicyId { get; set; }
}
