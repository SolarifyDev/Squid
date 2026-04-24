namespace Squid.Tentacle.Configuration;

public class KubernetesSettings
{
    public string Namespace { get; set; } = "default";
    public string PvcClaimName { get; set; } = "squid-tentacle-workspace";
    public bool UseScriptPods { get; set; } = false;

    // P0-C.1 (refactored Phase-3 to non-breaking three-mode pattern): default
    // remains the historical "bitnami/kubectl:latest" so existing deploys keep
    // working. ScriptPodImageValidator runs in Warn mode by default — emits a
    // structured Serilog warning at every pod creation pointing at the
    // SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT env var. Production deploys should
    // override with a digest-pinned reference (e.g. "image@sha256:<64-hex>") AND
    // set SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT=strict to refuse tag-only at startup.
    // See ScriptPodImageValidator + CLAUDE.md §"Hardening Three-Mode Enforcement".
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

    // Cleanup cycle intervals
    public int PendingCheckIntervalSeconds { get; set; } = 60;
    public int OrphanCleanupIntervalSeconds { get; set; } = 300;

    // Pending script queue bound
    public int MaxPendingScripts { get; set; } = 100;

    // Configurable event warning reasons
    public string AdditionalWarningReasons { get; set; } = "";

    // Item 1: Pod log encryption
    public bool EncryptPodLogs { get; set; } = true;

    // NFS watchdog force-kill grace period
    public int NfsForceKillGracePeriodSeconds { get; set; } = 30;

    // P2-1: Pod template ConfigMap fallback
    public string ScriptPodTemplateConfigMap { get; set; } = "";

    // P2-2: Dynamic image pull secret credentials
    public string ScriptPodRegistryServer { get; set; } = "";
    public string ScriptPodRegistryUsername { get; set; } = "";
    public string ScriptPodRegistryPassword { get; set; } = "";

    // R5-4: Multi-registry pull secrets
    // JSON array: [{"server":"registry.example.com","username":"user","password":"pass"}, ...]
    public string ScriptPodAdditionalRegistries { get; set; } = "";

    // R5-8: Proxy credentials via Secret reference
    public string ProxySecretName { get; set; } = "";
}
