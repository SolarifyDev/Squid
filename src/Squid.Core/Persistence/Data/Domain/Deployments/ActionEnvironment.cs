using Squid.Message.Domain;

namespace Squid.Core.Persistence.Data.Domain.Deployments;

public class ActionEnvironment : IEntity
{
    public int ActionId { get; set; }

    public int EnvironmentId { get; set; }
}
