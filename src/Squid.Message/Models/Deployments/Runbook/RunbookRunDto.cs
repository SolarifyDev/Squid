namespace Squid.Message.Models.Deployments.Runbook;

public class RunbookRunDto
{
    public int Id { get; set; }

    public int RunbookId { get; set; }

    public int SnapshotId { get; set; }

    public int SnapshotVersion { get; set; }

    public int TaskId { get; set; }

    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    public int EnvironmentId { get; set; }

    public int InitiatedBy { get; set; }

    public string State { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
}
