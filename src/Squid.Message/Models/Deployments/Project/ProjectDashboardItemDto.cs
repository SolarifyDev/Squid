namespace Squid.Message.Models.Deployments.Project;

public class ProjectDashboardItemDto
{
    public int DeploymentId { get; set; }
    public int ProjectId { get; set; }
    public int EnvironmentId { get; set; }
    public int ChannelId { get; set; }
    public string ChannelName { get; set; }
    public string ReleaseVersion { get; set; }
    public string State { get; set; }
    public DateTimeOffset? CompletedTime { get; set; }
    public bool HasWarningsOrErrors { get; set; }
}
