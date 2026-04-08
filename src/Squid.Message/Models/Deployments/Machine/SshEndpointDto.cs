using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Machine;

public class SshEndpointDto
{
    public string CommunicationStyle { get; set; }
    public string Host { get; set; }
    public int Port { get; set; } = 22;
    public string Fingerprint { get; set; }
    public string RemoteWorkingDirectory { get; set; }
    public SshProxyType ProxyType { get; set; }
    public string ProxyHost { get; set; }
    public int ProxyPort { get; set; }
    public string ProxyUsername { get; set; }
    public string ProxyPassword { get; set; }
    public List<EndpointResourceReference> ResourceReferences { get; set; }
}
