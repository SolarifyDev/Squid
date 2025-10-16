namespace Squid.Message.Domain.Deployments;

public class DeploymentActionProperty : IEntity
{
    public Guid ActionId { get; set; }

    public string PropertyName { get; set; }

    public string PropertyValue { get; set; }
}
