namespace Squid.Message.Domain.Deployments;

public class DeploymentAction : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid StepId { get; set; }

    public int ActionOrder { get; set; }

    public string Name { get; set; }

    public string ActionType { get; set; }

    public Guid? WorkerPoolId { get; set; }

    public bool IsDisabled { get; set; } = false;

    public bool IsRequired { get; set; } = true;

    public bool CanBeUsedForProjectVersioning { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
