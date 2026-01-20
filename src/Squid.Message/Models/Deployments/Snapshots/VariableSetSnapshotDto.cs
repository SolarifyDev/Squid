namespace Squid.Message.Models.Deployments.Snapshots;

public class VariableSetSnapshotDto
{
    public int Id { get; set; }
    
    public VariableSetSnapshotDataDto Data { get; set; } = new();
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBy { get; set; }
}
