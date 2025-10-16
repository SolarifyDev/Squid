namespace Squid.Message.Domain.Deployments;

public class ActionTenantTag : IEntity
{
    public Guid ActionId { get; set; }

    public string TenantTag { get; set; }
}
