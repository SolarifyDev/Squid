namespace Squid.Message.Domain.Deployments;

public class ActionTenantTag : IEntity
{
    public int ActionId { get; set; }

    public string TenantTag { get; set; }
}
