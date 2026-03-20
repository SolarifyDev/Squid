namespace Squid.Message.Models.Deployments.Snapshots;

public class VariableSetSnapshotDto
{
    public int Id { get; set; }

    public VariableSetSnapshotDataDto Data { get; set; } = new();

    public DateTimeOffset CreatedDate { get; set; }

    public int CreatedBy { get; set; }
}
