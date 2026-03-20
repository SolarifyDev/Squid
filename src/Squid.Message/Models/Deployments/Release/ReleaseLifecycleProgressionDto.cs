namespace Squid.Message.Models.Deployments.Release;

public class ReleaseLifecycleProgressionDto
{
    public int ReleaseId { get; set; }
    public string ReleaseVersion { get; set; }
    public int LifecycleId { get; set; }
    public string LifecycleName { get; set; }
    public List<ReleasePhaseProgressionDto> Phases { get; set; } = new();
}

public class ReleasePhaseProgressionDto
{
    public int PhaseId { get; set; }
    public string PhaseName { get; set; }
    public int SortOrder { get; set; }
    public bool IsComplete { get; set; }
    public bool IsOptional { get; set; }
    public string Progress { get; set; }
    public List<ReleasePhaseEnvironmentDto> Environments { get; set; } = new();
}

public class ReleasePhaseEnvironmentDto
{
    public int EnvironmentId { get; set; }
    public string EnvironmentName { get; set; }
    public bool IsAutomatic { get; set; }
    public bool CanDeploy { get; set; }
    public ReleaseEnvironmentDeploymentDto Deployment { get; set; }
}

public class ReleaseEnvironmentDeploymentDto
{
    public int DeploymentId { get; set; }
    public string State { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public DateTimeOffset? CompletedTime { get; set; }
}
