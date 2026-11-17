namespace Squid.Message.Models.Deployments.Project;

public class ProjectProgressionDto
{
    public List<ProjectDashboardEnvironmentDto> Environments { get; set; }
    public Dictionary<int, List<int>> ChannelEnvironments { get; set; }
    public List<ReleaseProgressionDto> Releases { get; set; }

    /// <summary>Total releases per channel id (the dashboard returns only a recent window of them).</summary>
    public Dictionary<int, int> ChannelReleaseCounts { get; set; }
}
