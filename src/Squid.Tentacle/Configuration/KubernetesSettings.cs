namespace Squid.Tentacle.Configuration;

public class KubernetesSettings
{
    public string Namespace { get; set; } = "default";
    public string PvcClaimName { get; set; } = "squid-tentacle-workspace";
    public bool UseScriptPods { get; set; } = false;
    public string ScriptPodImage { get; set; } = "bitnami/kubectl:latest";
    public string ScriptPodServiceAccount { get; set; } = "squid-script-sa";
    public string TentacleNamespace { get; set; } = "default";
    public int ScriptPodTimeoutSeconds { get; set; } = 1800;
    public string ScriptPodCpuRequest { get; set; } = "25m";
    public string ScriptPodMemoryRequest { get; set; } = "100Mi";
    public string ScriptPodCpuLimit { get; set; } = "500m";
    public string ScriptPodMemoryLimit { get; set; } = "512Mi";
    public long? ScriptPodRunAsUser { get; set; }
    public bool ScriptPodRunAsNonRoot { get; set; } = false;
    public string ScriptPodImagePullSecrets { get; set; } = "";
    public string ScriptPodTolerations { get; set; } = "";
    public string TentacleImage { get; set; } = "";
    public string ReleaseName { get; set; } = "";
}
