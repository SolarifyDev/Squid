namespace Squid.Message.Models.Deployments.Process;

public class DeploymentStepPropertyDto
{
    public Guid StepId { get; set; }
    
    public string PropertyName { get; set; }
    
    public string PropertyValue { get; set; }
}
