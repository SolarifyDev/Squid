namespace Squid.Message.Domain.Deployments;

public class DeploymentStepProperty : IEntity
{
    public int StepId { get; set; }

    public string PropertyName { get; set; }

    public string PropertyValue { get; set; }
}
