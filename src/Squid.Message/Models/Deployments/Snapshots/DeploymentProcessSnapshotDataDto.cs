namespace Squid.Message.Models.Deployments.Snapshots;

public class DeploymentProcessSnapshotDataDto
{
    public List<DeploymentStepSnapshotDataDto> StepSnapshots { get; set; } = new List<DeploymentStepSnapshotDataDto>();

    public Dictionary<string, List<string>> ScopeDefinitions { get; set; } = new Dictionary<string, List<string>>();
}