using Squid.Message.Models.Deployments.Process;

namespace Squid.Message.Models.Deployments.Snapshots;

public class DeploymentProcessSnapshotDto
{
    public int Id { get; set; }

    public int OriginalProcessId { get; set; }

    public int Version { get; set; }

    public string CreatedBy { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public DeploymentProcessSnapshotDataDto Data { get; set; }
}
