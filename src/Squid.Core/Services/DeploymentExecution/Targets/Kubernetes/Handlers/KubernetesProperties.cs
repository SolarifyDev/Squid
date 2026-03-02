namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesProperties
{
    // Deployment
    internal const string DeploymentResourceType = "Squid.Action.KubernetesContainers.DeploymentResourceType";
    internal const string DeploymentName = "Squid.Action.KubernetesContainers.DeploymentName";
    internal const string Namespace = "Squid.Action.KubernetesContainers.Namespace";
    internal const string Replicas = "Squid.Action.KubernetesContainers.Replicas";
    internal const string RevisionHistoryLimit = "Squid.Action.KubernetesContainers.RevisionHistoryLimit";
    internal const string ProgressDeadlineSeconds = "Squid.Action.KubernetesContainers.ProgressDeadlineSeconds";
    internal const string PodTerminationGracePeriodSeconds = "Squid.Action.KubernetesContainers.PodTerminationGracePeriodSeconds";
    internal const string PodPriorityClassName = "Squid.Action.KubernetesContainers.PodPriorityClassName";
    internal const string PodRestartPolicy = "Squid.Action.KubernetesContainers.PodRestartPolicy";
    internal const string PodDnsPolicy = "Squid.Action.KubernetesContainers.PodDnsPolicy";
    internal const string PodDnsNameservers = "Squid.Action.KubernetesContainers.PodDnsNameservers";
    internal const string PodDnsSearches = "Squid.Action.KubernetesContainers.PodDnsSearches";
    internal const string PodReadinessGates = "Squid.Action.KubernetesContainers.PodReadinessGates";
    internal const string ServiceAccountName = "Squid.Action.KubernetesContainers.ServiceAccountName";
    internal const string PodHostNetworking = "Squid.Action.KubernetesContainers.PodHostNetworking";
    internal const string PodSecurityFsGroup = "Squid.Action.KubernetesContainers.PodSecurityFsGroup";
    internal const string PodSecurityRunAsGroup = "Squid.Action.KubernetesContainers.PodSecurityRunAsGroup";
    internal const string PodSecurityRunAsUser = "Squid.Action.KubernetesContainers.PodSecurityRunAsUser";
    internal const string PodSecuritySupplementalGroups = "Squid.Action.KubernetesContainers.PodSecuritySupplementalGroups";
    internal const string PodSecurityRunAsNonRoot = "Squid.Action.KubernetesContainers.PodSecurityRunAsNonRoot";
    internal const string PodSecuritySeLinuxLevel = "Squid.Action.KubernetesContainers.PodSecuritySeLinuxLevel";
    internal const string PodSecuritySeLinuxRole = "Squid.Action.KubernetesContainers.PodSecuritySeLinuxRole";
    internal const string PodSecuritySeLinuxType = "Squid.Action.KubernetesContainers.PodSecuritySeLinuxType";
    internal const string PodSecuritySeLinuxUser = "Squid.Action.KubernetesContainers.PodSecuritySeLinuxUser";
    internal const string DeploymentStyle = "Squid.Action.KubernetesContainers.DeploymentStyle";
    internal const string MaxUnavailable = "Squid.Action.KubernetesContainers.MaxUnavailable";
    internal const string MaxSurge = "Squid.Action.KubernetesContainers.MaxSurge";
    internal const string DeploymentLabels = "Squid.Action.KubernetesContainers.DeploymentLabels";
    internal const string DeploymentAnnotations = "Squid.Action.KubernetesContainers.DeploymentAnnotations";
    internal const string PodAnnotations = "Squid.Action.KubernetesContainers.PodAnnotations";

    // Containers and volumes
    internal const string Containers = "Squid.Action.KubernetesContainers.Containers";
    internal const string CombinedVolumes = "Squid.Action.KubernetesContainers.CombinedVolumes";

    // Service
    internal const string ServiceName = "Squid.Action.KubernetesContainers.ServiceName";
    internal const string ServiceType = "Squid.Action.KubernetesContainers.ServiceType";
    internal const string ServiceClusterIp = "Squid.Action.KubernetesContainers.ServiceClusterIp";
    internal const string ServiceAnnotations = "Squid.Action.KubernetesContainers.ServiceAnnotations";
    internal const string ServicePorts = "Squid.Action.KubernetesContainers.ServicePorts";

    // ConfigMap
    internal const string ConfigMapName = "Squid.Action.KubernetesContainers.ConfigMapName";
    internal const string ConfigMapValues = "Squid.Action.KubernetesContainers.ConfigMapValues";

    // Ingress
    internal const string IngressName = "Squid.Action.KubernetesContainers.IngressName";
    internal const string IngressClassName = "Squid.Action.KubernetesContainers.IngressClassName";
    internal const string IngressAnnotations = "Squid.Action.KubernetesContainers.IngressAnnotations";
    internal const string IngressRules = "Squid.Action.KubernetesContainers.IngressRules";
    internal const string IngressTlsCertificates = "Squid.Action.KubernetesContainers.IngressTlsCertificates";

    // Secret
    internal const string SecretName = "Squid.Action.KubernetesContainers.SecretName";
    internal const string SecretValues = "Squid.Action.KubernetesContainers.SecretValues";

    // Scheduling and pod extras
    internal const string ObjectStatusCheck = "Squid.Action.KubernetesContainers.ObjectStatusCheck";
    internal const string HostAliases = "Squid.Action.KubernetesContainers.HostAliases";
    internal const string Tolerations = "Squid.Action.KubernetesContainers.Tolerations";
    internal const string NodeAffinity = "Squid.Action.KubernetesContainers.NodeAffinity";
    internal const string PodAffinity = "Squid.Action.KubernetesContainers.PodAffinity";
    internal const string PodAntiAffinity = "Squid.Action.KubernetesContainers.PodAntiAffinity";
    internal const string PodSecurityImagePullSecrets = "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets";
    internal const string PodSecuritySysctls = "Squid.Action.KubernetesContainers.PodSecuritySysctls";
    internal const string DnsConfigOptions = "Squid.Action.KubernetesContainers.DnsConfigOptions";

    // Legacy fallback
    internal const string LegacyNamespace = "Squid.Action.Kubernetes.Namespace";
}
