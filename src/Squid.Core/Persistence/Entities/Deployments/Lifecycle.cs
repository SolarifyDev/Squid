using Squid.Message.Enums.Deployments;

namespace Squid.Core.Persistence.Entities.Deployments;

public class Lifecycle : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Name { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    public byte[] DataVersion { get; set; } = Guid.NewGuid().ToByteArray();
    
    // Release retention (inline)
    public int ReleaseRetentionQuantity { get; set; }
    public bool ReleaseRetentionKeepForever { get; set; } = true;
    public RetentionPolicyUnit ReleaseRetentionUnit { get; set; }

    // Tentacle retention (inline)
    public int TentacleRetentionQuantity { get; set; }
    public bool TentacleRetentionKeepForever { get; set; } = true;
    public RetentionPolicyUnit TentacleRetentionUnit { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
