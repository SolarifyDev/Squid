using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments.LifeCycle;

public class LifeCycleDto
{
    public int Id { get; set; }

    public string Name { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    // Release retention
    public RetentionPolicyUnit ReleaseRetentionUnit { get; set; }
    public int ReleaseRetentionQuantity { get; set; }
    public bool ReleaseRetentionKeepForever { get; set; } = true;

    // Tentacle retention
    public RetentionPolicyUnit TentacleRetentionUnit { get; set; }
    public int TentacleRetentionQuantity { get; set; }
    public bool TentacleRetentionKeepForever { get; set; } = true;
}
