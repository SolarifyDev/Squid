using Squid.Message.Models.Deployments.Process;

namespace Squid.Message.Models.Deployments.Snapshots;

public class DeploymentStepSnapshotDataDto
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string StepType { get; set; }

    public int StepOrder { get; set; }

    public string Condition { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

    public List<DeploymentActionSnapshotDataDto> ActionSnapshots { get; set; } = new List<DeploymentActionSnapshotDataDto>();
}
