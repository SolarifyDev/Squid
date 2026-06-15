using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Machine;

public class SshEndpointDto
{
    /// <summary>KDF scope used when encrypting/decrypting <see cref="ProxyPassword"/> at rest. The V2
    /// envelope uses a random per-payload salt so this value is not security-relevant — it is a shared
    /// constant so the write seam (machine register/update) and the read seams (deploy variable
    /// contributor, SSH health check) agree. Mirrors the KdfScope=0 the column-based providers use.</summary>
    public const int ProxyPasswordKdfScope = 0;

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
