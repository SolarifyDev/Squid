namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentStep : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int ProcessId { get; set; }

    public int StepOrder { get; set; }

    public string Name { get; set; }

    public string StepType { get; set; }

    public string Condition { get; set; }

    public string StartTrigger { get; set; }

    public string PackageRequirement { get; set; }

    public bool IsDisabled { get; set; } = false;

    public bool IsRequired { get; set; } = true;

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
