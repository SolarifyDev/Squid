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
    public string NfsWatchdogImage { get; set; } = "";
    public bool IsolateWorkspaceToEmptyDir { get; set; } = false;
    public string PersistenceAccessMode { get; set; } = "ReadWriteMany";
    public int PendingPodTimeoutMinutes { get; set; } = 5;
    public int OrphanCleanupMinutes { get; set; } = 10;
    public int IsolationMutexTimeoutMinutes { get; set; } = 30;
    public long MaxLogBufferBytes { get; set; } = 10 * 1024 * 1024; // 10 MB
    public bool RawScriptMode { get; set; } = false;
    public string HttpProxy { get; set; } = "";
    public string HttpsProxy { get; set; } = "";
    public string NoProxy { get; set; } = "";

    // Item 4: Pod deletion grace period
    public int ScriptPodGracePeriodSeconds { get; set; } = 600;
    public int OrphanPodGracePeriodSeconds { get; set; } = 30;

    // Item 2: NFS watchdog force-kill
    public int NfsWatchdogForceKillThreshold { get; set; } = 3;

    // Item 5: Custom labels/annotations
    public string ScriptPodLabels { get; set; } = "";
    public string ScriptPodAnnotations { get; set; } = "";

    // Item 7: Platform-aware scheduling
    public string ScriptPodNodeArchitecture { get; set; } = "";
    public string ScriptPodNodeSelector { get; set; } = "";

    // Item 1: Pod log encryption
    public bool EncryptPodLogs { get; set; } = false;
}
