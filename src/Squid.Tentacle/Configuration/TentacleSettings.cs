namespace Squid.Tentacle.Configuration;

public class TentacleSettings
{
    public const string DefaultKubernetesAgentChartRef = "oci://registry-1.docker.io/squidcd/kubernetes-agent";

    public string Flavor { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = "https://localhost:7078";
    public string ServerCommsUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ServerCertificate { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Roles { get; set; } = string.Empty;
    public int SpaceId { get; set; } = 1;
    public string Environments { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = "/squid/work";
    public string CertsPath { get; set; } = "/squid/certs";
    public int HealthCheckPort { get; set; } = 8080;
    public int ListeningPort { get; set; } = 10933;
    public string ListeningHostName { get; set; } = string.Empty;

    /// <summary>
    /// Strategy for resolving the Tentacle's externally-reachable hostname
    /// during Listening-mode registration. One of:
    /// <c>Custom</c> (use <see cref="ListeningHostName"/>),
    /// <c>ComputerName</c> (Dns.GetHostName — legacy default),
    /// <c>FQDN</c> (reverse-DNS of local host),
    /// <c>PublicIp</c> (AWS/Azure/GCE instance metadata).
    /// Aligned with Octopus's <c>PublicHostNameConfiguration</c>.
    /// </summary>
    public string PublicHostNameConfiguration { get; set; } = string.Empty;

    public string SubscriptionId { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string ReleaseName { get; set; } = string.Empty;
    public string HelmNamespace { get; set; } = string.Empty;
    public string ChartRef { get; set; } = DefaultKubernetesAgentChartRef;
    public int PollingConnectionCount { get; set; } = 5;
    public string ServerCommsAddresses { get; set; } = string.Empty;
    public int ShutdownDrainTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Explicit flag set to <c>"true"</c> by the <c>register</c> command after a
    /// successful registration. Used by <see cref="LinuxTentacleFlavor.ResolveRegistrar"/>
    /// to distinguish "already registered → skip" from "user supplied ServerCertificate
    /// for TLS pinning but hasn't registered yet" (Docker first-run scenario).
    ///
    /// Without this, a Docker container started with both <c>Tentacle__ApiKey</c> and
    /// <c>Tentacle__ServerCertificate</c> would skip registration entirely, leaving the
    /// Server unaware of the Tentacle and all poll connections rejected.
    /// </summary>
    public string Registered { get; set; } = string.Empty;

    /// <summary>
    /// Optional outbound HTTP CONNECT proxy for Polling connections to the
    /// Server. Mirrors Octopus's <c>PollingProxyConfiguration</c>. When
    /// <see cref="ProxySettings.Host"/> is empty the Tentacle connects
    /// directly. Supports anonymous and username/password-authenticated
    /// proxies, which covers Squid/Zscaler/BlueCoat enterprise deployments.
    /// </summary>
    public ProxySettings Proxy { get; set; } = new();

    public List<string> GetServerCommsUrls()
    {
        if (!string.IsNullOrWhiteSpace(ServerCommsAddresses))
            return ServerCommsAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (!string.IsNullOrWhiteSpace(ServerCommsUrl))
            return new List<string> { ServerCommsUrl };

        return new List<string>();
    }
}

/// <summary>
/// Outbound HTTP proxy configuration used by Polling connections and by the
/// Listening-mode registration HTTP calls. Flat structure so it maps cleanly
/// to <c>Tentacle:Proxy:*</c> env vars and CLI args (<c>--proxy-host</c>,
/// <c>--proxy-port</c>, <c>--proxy-user</c>, <c>--proxy-password</c>).
/// </summary>
public sealed class ProxySettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 0;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && Port > 0;
}
