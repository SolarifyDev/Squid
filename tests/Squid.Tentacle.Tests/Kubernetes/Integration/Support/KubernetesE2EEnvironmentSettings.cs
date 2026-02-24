using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Kubernetes.Integration.Support;

public sealed class KubernetesE2EEnvironmentSettings
{
    public bool Enabled { get; init; }
    public string ClusterName { get; init; } = "squid-tentacle-e2e";
    public string Namespace { get; init; } = "squid-agent-e2e";
    public string ReleaseName { get; init; } = "squid-tentacle";
    public string TentacleImageRepository { get; init; } = string.Empty;
    public string TentacleImageTag { get; init; } = string.Empty;
    public string ScriptPodImage { get; init; } = string.Empty;
    public string ServerUrl { get; init; } = string.Empty;
    public string BearerToken { get; init; } = string.Empty;
    public string WorkspaceStorageClassName { get; init; } = string.Empty;
    public string KubernetesTargetNamespace { get; init; } = "default";
    public bool CreateKindCluster { get; init; } = false;
    public bool CleanupKindCluster { get; init; } = false;
    public bool RunFaultScenarios { get; init; } = false;
    public string TentacleDeploymentName { get; init; } = string.Empty;
    public string TentaclePodLabelSelector { get; init; } = string.Empty;
    public string ScriptPodLabelSelector { get; init; } = string.Empty;

    public bool HasRequiredInstallSettings =>
        !string.IsNullOrWhiteSpace(TentacleImageRepository)
        && !string.IsNullOrWhiteSpace(TentacleImageTag)
        && !string.IsNullOrWhiteSpace(ScriptPodImage)
        && !string.IsNullOrWhiteSpace(ServerUrl)
        && !string.IsNullOrWhiteSpace(BearerToken);

    public bool CanRunInstallSmoke(KubernetesIntegrationPrerequisites prereqs)
        => Enabled && prereqs.IsAvailable && HasRequiredInstallSettings;

    public bool CanRunFaultScenarioSmoke(KubernetesIntegrationPrerequisites prereqs)
        => CanRunInstallSmoke(prereqs) && RunFaultScenarios;

    public string DescribeMissingInstallSettings()
    {
        var missing = new List<string>();
        if (!Enabled) missing.Add("SQUID_TENTACLE_K8S_E2E_ENABLED=1");
        if (string.IsNullOrWhiteSpace(TentacleImageRepository)) missing.Add("SQUID_TENTACLE_K8S_E2E_TENTACLE_IMAGE_REPOSITORY");
        if (string.IsNullOrWhiteSpace(TentacleImageTag)) missing.Add("SQUID_TENTACLE_K8S_E2E_TENTACLE_IMAGE_TAG");
        if (string.IsNullOrWhiteSpace(ScriptPodImage)) missing.Add("SQUID_TENTACLE_K8S_E2E_SCRIPT_POD_IMAGE");
        if (string.IsNullOrWhiteSpace(ServerUrl)) missing.Add("SQUID_TENTACLE_K8S_E2E_SERVER_URL");
        if (string.IsNullOrWhiteSpace(BearerToken)) missing.Add("SQUID_TENTACLE_K8S_E2E_BEARER_TOKEN");
        return string.Join(", ", missing);
    }

    public static KubernetesE2EEnvironmentSettings Load()
    {
        static string ReadRaw(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;
        static string ReadWithDefault(string name, string fallback)
            => string.IsNullOrWhiteSpace(ReadRaw(name)) ? fallback : ReadRaw(name);
        static bool ReadBool(string name)
        {
            var value = ReadRaw(name);
            if (string.IsNullOrWhiteSpace(value))
                return false;
            return value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        return new KubernetesE2EEnvironmentSettings
        {
            Enabled = ReadBool("SQUID_TENTACLE_K8S_E2E_ENABLED"),
            ClusterName = ReadWithDefault("SQUID_TENTACLE_K8S_E2E_KIND_CLUSTER_NAME", "squid-tentacle-e2e"),
            Namespace = ReadWithDefault("SQUID_TENTACLE_K8S_E2E_NAMESPACE", "squid-agent-e2e"),
            ReleaseName = ReadWithDefault("SQUID_TENTACLE_K8S_E2E_RELEASE_NAME", "squid-tentacle"),
            TentacleImageRepository = ReadRaw("SQUID_TENTACLE_K8S_E2E_TENTACLE_IMAGE_REPOSITORY"),
            TentacleImageTag = ReadRaw("SQUID_TENTACLE_K8S_E2E_TENTACLE_IMAGE_TAG"),
            ScriptPodImage = ReadRaw("SQUID_TENTACLE_K8S_E2E_SCRIPT_POD_IMAGE"),
            ServerUrl = ReadRaw("SQUID_TENTACLE_K8S_E2E_SERVER_URL"),
            BearerToken = ReadRaw("SQUID_TENTACLE_K8S_E2E_BEARER_TOKEN"),
            WorkspaceStorageClassName = ReadRaw("SQUID_TENTACLE_K8S_E2E_STORAGE_CLASS"),
            KubernetesTargetNamespace = ReadWithDefault("SQUID_TENTACLE_K8S_E2E_TARGET_NAMESPACE", "default"),
            CreateKindCluster = ReadBool("SQUID_TENTACLE_K8S_E2E_CREATE_KIND_CLUSTER"),
            CleanupKindCluster = ReadBool("SQUID_TENTACLE_K8S_E2E_CLEANUP_KIND_CLUSTER"),
            RunFaultScenarios = ReadBool("SQUID_TENTACLE_K8S_E2E_RUN_FAULT_SCENARIOS"),
            TentacleDeploymentName = ReadRaw("SQUID_TENTACLE_K8S_E2E_TENTACLE_DEPLOYMENT_NAME"),
            TentaclePodLabelSelector = ReadRaw("SQUID_TENTACLE_K8S_E2E_TENTACLE_POD_LABEL_SELECTOR"),
            ScriptPodLabelSelector = ReadRaw("SQUID_TENTACLE_K8S_E2E_SCRIPT_POD_LABEL_SELECTOR")
        };
    }

    public string GetTentacleDeploymentName()
        => string.IsNullOrWhiteSpace(TentacleDeploymentName) ? ReleaseName : TentacleDeploymentName;

    public string GetTentaclePodLabelSelector()
    {
        if (!string.IsNullOrWhiteSpace(TentaclePodLabelSelector))
            return TentaclePodLabelSelector;

        return $"app.kubernetes.io/instance={ReleaseName},app.kubernetes.io/name=squid-tentacle";
    }
}
