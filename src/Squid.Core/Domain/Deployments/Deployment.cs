namespace Squid.Core.Domain.Deployments;

public class Deployment: IEntity<Guid>
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid ReleaseId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ChannelId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? DeployedBy { get; set; }
    public DateTime? DeployedAt { get; set; }
}