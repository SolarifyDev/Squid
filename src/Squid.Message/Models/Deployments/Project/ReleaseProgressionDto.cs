namespace Squid.Message.Models.Deployments.Project;

public class ReleaseProgressionDto
{
    public ReleaseProgressionReleaseDto Release { get; set; }
    public ReleaseProgressionChannelDto Channel { get; set; }
    public Dictionary<int, List<ReleaseProgressionDeploymentDto>> Deployments { get; set; }
    public List<int> NextDeployments { get; set; }
}

public class ReleaseProgressionReleaseDto
{
    public int Id { get; set; }
    public string Version { get; set; }
    public DateTimeOffset Assembled { get; set; }
    public int ChannelId { get; set; }
}

public class ReleaseProgressionChannelDto
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class ReleaseProgressionDeploymentDto
{
    public int DeploymentId { get; set; }
    public string State { get; set; }
    public string ReleaseVersion { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? CompletedTime { get; set; }
    public bool HasWarningsOrErrors { get; set; }
    public bool IsCurrent { get; set; }
}
