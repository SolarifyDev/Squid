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
    public string SubscriptionId { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string ReleaseName { get; set; } = string.Empty;
    public string HelmNamespace { get; set; } = string.Empty;
    public string ChartRef { get; set; } = DefaultKubernetesAgentChartRef;
}
