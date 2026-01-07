namespace Squid.Core.Persistence.Entities.Deployments;

public class ActionEnvironment : IEntity
{
    public int ActionId { get; set; }

    public int EnvironmentId { get; set; }
}
