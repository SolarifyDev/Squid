using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Machine;

public class EndpointResourceReference
{
    public EndpointResourceType Type { get; set; }
    public int ResourceId { get; set; }
}
