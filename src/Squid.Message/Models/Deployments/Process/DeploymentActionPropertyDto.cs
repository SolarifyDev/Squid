namespace Squid.Message.Models.Deployments.Process;

public class DeploymentActionPropertyDto
{
    public Guid ActionId { get; set; }
    
    public string PropertyName { get; set; }
    
    public string PropertyValue { get; set; }
}
