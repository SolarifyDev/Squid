namespace Squid.Core.Persistence.Entities.Deployments;

using Squid.Message.Enums;

public class DeploymentProcess : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int Version { get; set; } = 1;

    public int SpaceId { get; set; }

    public DeploymentProcessKind ProcessKind { get; set; } = DeploymentProcessKind.ProjectDeployment;

    public int? OwnerId { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
