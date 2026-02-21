namespace Squid.Agent.Configuration;

public class AgentSettings
{
    public string ServerUrl { get; set; } = "https://localhost:7078";
    public int ServerPollingPort { get; set; } = 10943;
    public string BearerToken { get; set; } = string.Empty;
    public string ServerCertificate { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    public string Roles { get; set; } = "k8s";
    public string MachineName { get; set; } = string.Empty;
    public int SpaceId { get; set; } = 1;
    public string EnvironmentIds { get; set; } = string.Empty;

    // PVC paths
    public string WorkspacePath { get; set; } = "/squid/work";
    public string CertsPath { get; set; } = "/squid/certs";
    public string PvcClaimName { get; set; } = "squid-agent-workspace";

    // Script Pod settings
    public string ScriptPodImage { get; set; } = "bitnami/kubectl:latest";
    public string ScriptPodServiceAccount { get; set; } = "squid-script-sa";
    public string AgentNamespace { get; set; } = "default";
    public int ScriptPodTimeoutSeconds { get; set; } = 1800;
    public string ScriptPodCpuRequest { get; set; } = "25m";
    public string ScriptPodMemoryRequest { get; set; } = "100Mi";
    public string ScriptPodCpuLimit { get; set; } = "500m";
    public string ScriptPodMemoryLimit { get; set; } = "512Mi";

    // Execution mode
    public bool UseScriptPods { get; set; } = false;
}
