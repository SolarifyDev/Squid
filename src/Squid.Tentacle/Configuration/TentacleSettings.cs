namespace Squid.Tentacle.Configuration;

public class TentacleSettings
{
    public const string DefaultKubernetesAgentChartRef = "oci://registry-1.docker.io/squidcd/kubernetes-agent";

    /// <summary>
    /// Sentinel value of <see cref="ServerUrl"/> that means "operator hasn't
    /// configured a real server yet, skip auto-registration". Used by the listening
    /// registrar and the <c>register</c> CLI command.
    ///
    /// <para>P0-T.6 (2026-04-24 audit): pre-fix, two call sites compared
    /// <see cref="ServerUrl"/> against this literal verbatim. Now the literal lives in
    /// one place and the comparison goes through
    /// <see cref="IsAutoRegistrationUnconfigured"/> so drift between call sites is
    /// impossible. Pinned by
    /// <c>TentacleSettingsSentinelTests.DefaultServerUrlSentinel_Pinned</c>.</para>
    /// </summary>
    public const string DefaultServerUrlSentinel = "https://localhost:7078";

    public string Flavor { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = DefaultServerUrlSentinel;
    public string ServerCommsUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ServerCertificate { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Roles { get; set; } = string.Empty;
    public int SpaceId { get; set; } = 1;
    public string Environments { get; set; } = string.Empty;
    // Empty = "resolve at runtime via InstanceSelector.ResolveCertsPath". The
    // old "/squid/certs" / "/squid/work" defaults were Docker-image conventions
    // that only worked for images pre-creating those dirs. A native systemd
    // install running as the `squid-tentacle` service user could not write to
    // / (root), so the agent crashed at startup with UnauthorizedAccessException.
    // RunCommand fills these in from the per-platform instance paths when left
    // blank — see RunCommandPathResolver.
    public string WorkspacePath { get; set; } = string.Empty;
    public string CertsPath { get; set; } = string.Empty;
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

    /// <summary>
    /// P0-T.6 (2026-04-24 audit): single source of truth for "is this ServerUrl still
    /// the default-unconfigured sentinel or blank?". Both
    /// <c>TentacleListeningRegistrar.RegisterAsync</c> (silently skip) and
    /// <c>RegisterCommand.ExecuteAsync</c> (refuse with usage help) route through here
    /// so a drift in one place doesn't silently desync the other.
    ///
    /// <para>Returns <c>true</c> only for null / whitespace-only / the exact sentinel.
    /// Any other value — including <c>http://localhost:7078</c> and
    /// <c>https://localhost:9999</c> — is treated as a real configuration the operator
    /// wants registered. This unblocks legitimate local-dev deploys that pre-fix were
    /// silently dropped by the magic-string compare.</para>
    /// </summary>
    public static bool IsAutoRegistrationUnconfigured(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return true;

        return string.Equals(serverUrl, DefaultServerUrlSentinel, StringComparison.Ordinal);
    }

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
