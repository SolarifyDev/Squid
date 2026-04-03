using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class RegisterOpenClawCommand : ICommand, ISpaceScoped
{
    public string MachineName { get; set; }
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public List<string> Roles { get; set; }
    public List<int> EnvironmentIds { get; set; }

    // Endpoint
    public string BaseUrl { get; set; }

    // Inline tokens (optional — fallback when no account)
    public string InlineGatewayToken { get; set; }
    public string InlineHooksToken { get; set; }

    // WebSocket URL (optional — if not set, derived from BaseUrl)
    public string WebSocketUrl { get; set; }

    // Resources — optional (AuthenticationAccount for OpenClawGateway)
    public List<EndpointResourceReference> ResourceReferences { get; set; }
}
