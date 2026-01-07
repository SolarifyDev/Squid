namespace Squid.Core.Persistence.Entities.Deployments;

public class ActionChannel : IEntity
{
    public int ActionId { get; set; }

    public int ChannelId { get; set; }
}
