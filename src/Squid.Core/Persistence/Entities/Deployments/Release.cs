namespace Squid.Core.Persistence.Entities.Deployments;

public class Release : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string Version { get; set; }

    public int ProjectId { get; set; }

    public int ProjectVariableSetSnapshotId { get; set; }

    public int ProjectDeploymentProcessSnapshotId { get; set; }

    public int ChannelId { get; set; }

    public int SpaceId { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
