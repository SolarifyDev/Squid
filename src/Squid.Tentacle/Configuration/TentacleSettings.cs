namespace Squid.Tentacle.Configuration;

public class TentacleSettings
{
    public string ServerUrl { get; set; } = "https://localhost:7078";
    public int ServerPollingPort { get; set; } = 10943;
    public string BearerToken { get; set; } = string.Empty;
    public string ServerCertificate { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Roles { get; set; } = "k8s";
    public int SpaceId { get; set; } = 1;
    public string EnvironmentIds { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = "/squid/work";
    public string CertsPath { get; set; } = "/squid/certs";
    public int HealthCheckPort { get; set; } = 8080;
}
