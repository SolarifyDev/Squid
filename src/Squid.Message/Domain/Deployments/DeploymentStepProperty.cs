namespace Squid.Message.Domain.Deployments;

public class DeploymentStepProperty : IEntity
{
    public Guid StepId { get; set; }

    public string PropertyName { get; set; }

    public string PropertyValue { get; set; }
}
