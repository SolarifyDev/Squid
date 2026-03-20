namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentAction : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int StepId { get; set; }

    public int ActionOrder { get; set; }

    public string Name { get; set; }

    public string ActionType { get; set; }

    public int? WorkerPoolId { get; set; }

    public int? FeedId { get; set; }

    public string PackageId { get; set; }

    public bool IsDisabled { get; set; } = false;

    public bool IsRequired { get; set; } = true;

    public bool CanBeUsedForProjectVersioning { get; set; } = false;

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
