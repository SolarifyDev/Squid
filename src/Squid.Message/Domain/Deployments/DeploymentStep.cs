namespace Squid.Message.Domain.Deployments;

public class DeploymentStep : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid ProcessId { get; set; }

    public int StepOrder { get; set; }

    public string Name { get; set; }

    public string StepType { get; set; }

    public string Condition { get; set; }

    public string StartTrigger { get; set; }

    public string PackageRequirement { get; set; }

    public bool IsDisabled { get; set; } = false;

    public bool IsRequired { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
