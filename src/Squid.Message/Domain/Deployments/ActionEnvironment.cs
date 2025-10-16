namespace Squid.Message.Domain.Deployments;

public class ActionEnvironment : IEntity
{
    public Guid ActionId { get; set; }

    public Guid EnvironmentId { get; set; }
}
