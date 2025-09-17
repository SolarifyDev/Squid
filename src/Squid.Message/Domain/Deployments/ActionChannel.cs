namespace Squid.Message.Domain.Deployments;

public class ActionChannel : IEntity
{
    public Guid ActionId { get; set; }

    public Guid ChannelId { get; set; }
}
