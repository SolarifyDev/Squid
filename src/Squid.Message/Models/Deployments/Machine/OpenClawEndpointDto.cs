namespace Squid.Message.Models.Deployments.Machine;

public class OpenClawEndpointDto
{
    public string CommunicationStyle { get; set; }
    public string BaseUrl { get; set; }
    public string InlineGatewayToken { get; set; }
    public string InlineHooksToken { get; set; }
    public string WebSocketUrl { get; set; }
    public List<EndpointResourceReference> ResourceReferences { get; set; }
}
