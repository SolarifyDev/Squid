namespace Squid.Message.Models.Deployments.Process;

public class DeploymentStepPropertyDto
{
    public int Id { get; set; }

    public int StepId { get; set; }

    public string PropertyName { get; set; }

    public string PropertyValue { get; set; }
}
