namespace Squid.Message.Domain.Deployments;

public class Lifecycle : IEntity<int>
{
    public int Id { get; set; }

    public string Name { get; set; }

    public byte[] DataVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public int SpaceId { get; set; }

    public string Slug { get; set; }
    
    public int ReleaseRetentionPolicyId { get; set; }
    
    public int TentacleRetentionPolicyId { get; set; }
}
