using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution;

public class EndpointResourceReferences
{
    public List<EndpointResourceReference> References { get; set; } = new();

    public int? FindFirst(EndpointResourceType type) => References.FirstOrDefault(r => r.Type == type)?.ResourceId;
}
