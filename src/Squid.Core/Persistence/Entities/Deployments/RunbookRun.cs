namespace Squid.Core.Persistence.Entities.Deployments;

public class RunbookRun : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int RunbookId { get; set; }

    public int SnapshotId { get; set; }

    public int ServerTaskId { get; set; }

    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    public int EnvironmentId { get; set; }

    public int InitiatedBy { get; set; }

    public ServerTask Task { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
