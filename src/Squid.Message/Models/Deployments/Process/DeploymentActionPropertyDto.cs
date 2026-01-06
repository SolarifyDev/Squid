namespace Squid.Message.Models.Deployments.Process;

public class DeploymentActionPropertyDto
{
    public int Id { get; set; }

    public int ActionId { get; set; }

    public string PropertyName { get; set; }

    public string PropertyValue { get; set; }
}
