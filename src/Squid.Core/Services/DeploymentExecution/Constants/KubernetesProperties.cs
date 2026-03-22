namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesProperties
{
    // Deployment
    // DeploymentResourceType: reserved for future use — accepted from the frontend (Deployment/StatefulSet/DaemonSet/Job)
    // but the generator currently always produces kind: Deployment.
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
    // ObjectStatusCheck: reserved for future use — accepted from the frontend but not consumed by any generator.
    // When implemented, it will control whether the deployment waits for pod readiness before marking success.
    internal const string ObjectStatusCheck = "Squid.Action.KubernetesContainers.ObjectStatusCheck";
    internal const string HostAliases = "Squid.Action.KubernetesContainers.HostAliases";
    internal const string Tolerations = "Squid.Action.KubernetesContainers.Tolerations";
    internal const string NodeAffinity = "Squid.Action.KubernetesContainers.NodeAffinity";
    internal const string PodAffinity = "Squid.Action.KubernetesContainers.PodAffinity";
    internal const string PodAntiAffinity = "Squid.Action.KubernetesContainers.PodAntiAffinity";
    internal const string PodSecurityImagePullSecrets = "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets";
    internal const string PodSecuritySysctls = "Squid.Action.KubernetesContainers.PodSecuritySysctls";
    internal const string DnsConfigOptions = "Squid.Action.KubernetesContainers.DnsConfigOptions";

    // Server-side apply
    internal const string ServerSideApplyEnabled = "Squid.Action.Kubernetes.ServerSideApply.Enabled";
    internal const string ServerSideApplyFieldManager = "Squid.Action.Kubernetes.ServerSideApply.FieldManager";
    internal const string ServerSideApplyForceConflicts = "Squid.Action.Kubernetes.ServerSideApply.ForceConflicts";

    // Resource status check
    internal const string ObjectStatusCheckTimeout = "Squid.Action.KubernetesContainers.ObjectStatusCheckTimeout";

    // Legacy fallback
    internal const string LegacyNamespace = "Squid.Action.Kubernetes.Namespace";
}

/// <summary>
/// Keys inside <see cref="KubernetesProperties.Containers"/> JSON payload.
/// These are container item fields (not action property names).
/// </summary>
internal static class KubernetesContainerPayloadProperties
{
    // Container basic fields
    internal const string Name = "Name";
    internal const string Image = "Image";
    internal const string PackageId = "PackageId";
    internal const string FeedId = "FeedId";
    internal const string CreateFeedSecrets = "CreateFeedSecrets";
    internal const string IsInitContainer = "IsInitContainer";

    // Nested objects/arrays
    internal const string Ports = "Ports";
    internal const string Resources = "Resources";
    internal const string VolumeMounts = "VolumeMounts";
    internal const string ConfigMapEnvFromSource = "ConfigMapEnvFromSource";
    internal const string LivenessProbe = "LivenessProbe";
    internal const string ReadinessProbe = "ReadinessProbe";
    internal const string StartupProbe = "StartupProbe";
    internal const string SecurityContext = "SecurityContext";
    internal const string Lifecycle = "Lifecycle";

    // Environment variables
    internal const string EnvironmentVariables = "EnvironmentVariables";
    internal const string SecretEnvironmentVariables = "SecretEnvironmentVariables";
    internal const string ConfigMapEnvironmentVariables = "ConfigMapEnvironmentVariables";
    internal const string FieldRefEnvironmentVariables = "FieldRefEnvironmentVariables";
    internal const string SecretEnvFromSource = "SecretEnvFromSource";

    // Container settings
    internal const string ImagePullPolicy = "ImagePullPolicy";
    internal const string TerminationMessagePath = "TerminationMessagePath";
    internal const string TerminationMessagePolicy = "TerminationMessagePolicy";
    internal const string Command = "Command";
    internal const string Args = "Args";
}

/// <summary>
/// Keys for each item inside Containers[].Ports.
/// </summary>
internal static class KubernetesContainerPortPayloadProperties
{
    internal const string Name = "key";
    internal const string ContainerPort = "value";
    internal const string Protocol = "option";
}

/// <summary>
/// Keys for Containers[].Resources.
/// </summary>
internal static class KubernetesContainerResourcePayloadProperties
{
    internal const string Requests = "requests";
    internal const string Limits = "limits";
    internal const string Cpu = "cpu";
    internal const string Memory = "memory";
}

/// <summary>
/// Keys for each item inside Containers[].VolumeMounts.
/// </summary>
internal static class KubernetesContainerVolumeMountPayloadProperties
{
    internal const string VolumeName = "key";
    internal const string MountPath = "value";
    internal const string SubPath = "option";
}

/// <summary>
/// Keys for each item inside Containers[].ConfigMapEnvFromSource.
/// </summary>
internal static class KubernetesContainerEnvFromPayloadProperties
{
    internal const string Name = "key";
    internal const string Prefix = "value";
    internal const string Optional = "option";
}

/// <summary>
/// Keys for Containers[].EnvironmentVariables items.
/// </summary>
internal static class KubernetesContainerEnvVarPayloadProperties
{
    internal const string Key = "key";
    internal const string Value = "value";
}

/// <summary>
/// Keys for Containers[].ConfigMapEnvironmentVariables / SecretEnvironmentVariables items.
/// </summary>
internal static class KubernetesContainerEnvVarSourcePayloadProperties
{
    internal const string Key = "key";
    internal const string Value = "value";
    internal const string Option = "option";
    internal const string Optional = "optional";
}

/// <summary>
/// Keys for Containers[].FieldRefEnvironmentVariables items.
/// </summary>
internal static class KubernetesContainerFieldRefPayloadProperties
{
    internal const string Key = "key";
    internal const string Value = "value";
}

/// <summary>
/// Keys for Containers[].SecretEnvFromSource items.
/// </summary>
internal static class KubernetesContainerSecretEnvFromPayloadProperties
{
    internal const string Name = "key";
    internal const string Prefix = "value";
    internal const string Optional = "option";
}

/// <summary>
/// Keys for Containers[].SecurityContext.
/// </summary>
internal static class KubernetesContainerSecurityContextPayloadProperties
{
    internal const string RunAsUser = "runAsUser";
    internal const string RunAsGroup = "runAsGroup";
    internal const string RunAsNonRoot = "runAsNonRoot";
    internal const string ReadOnlyRootFilesystem = "readOnlyRootFilesystem";
    internal const string AllowPrivilegeEscalation = "allowPrivilegeEscalation";
    internal const string Privileged = "privileged";
}

/// <summary>
/// Keys for Containers[].Lifecycle.
/// </summary>
internal static class KubernetesContainerLifecyclePayloadProperties
{
    internal const string PostStart = "postStart";
    internal const string PreStop = "preStop";
    internal const string Type = "type";
    internal const string Command = "command";
}

/// <summary>
/// Keys for Containers[].{LivenessProbe, ReadinessProbe, StartupProbe}.
/// </summary>
internal static class KubernetesContainerProbePayloadProperties
{
    internal const string Type = "type";
    internal const string HttpGet = "httpGet";
    internal const string TcpSocket = "tcpSocket";
    internal const string Exec = "exec";
    internal const string InitialDelaySeconds = "initialDelaySeconds";
    internal const string PeriodSeconds = "periodSeconds";
    internal const string TimeoutSeconds = "timeoutSeconds";
    internal const string SuccessThreshold = "successThreshold";
    internal const string FailureThreshold = "failureThreshold";
}

/// <summary>
/// Generic key/value item keys used by list editors.
/// </summary>
internal static class KubernetesKeyValuePayloadProperties
{
    internal const string PascalKey = "Key";
    internal const string PascalValue = "Value";
    internal const string LowerKey = "key";
    internal const string LowerValue = "value";
}

/// <summary>
/// Keys for each item inside ServicePorts payload.
/// </summary>
internal static class KubernetesServicePortPayloadProperties
{
    internal const string Name = "name";
    internal const string Port = "port";
    internal const string TargetPort = "targetPort";
    internal const string NodePort = "nodePort";
    internal const string Protocol = "protocol";
}

/// <summary>
/// Keys for each item inside CombinedVolumes payload.
/// </summary>
internal static class KubernetesVolumePayloadProperties
{
    internal const string Name = "Name";
    internal const string Type = "Type";
    internal const string ReferenceName = "ReferenceName";
}

/// <summary>
/// Volume type values used in CombinedVolumes payload.
/// </summary>
internal static class KubernetesVolumeTypeValues
{
    internal const string ConfigMap = "ConfigMap";
    internal const string Secret = "Secret";
    internal const string EmptyDir = "EmptyDir";
    internal const string PersistentVolumeClaim = "PVC";
    internal const string HostPath = "HostPath";
}

/// <summary>
/// Nested keys under probe/httpGet/tcpSocket/exec payloads.
/// </summary>
internal static class KubernetesProbeActionPayloadProperties
{
    internal const string Command = "command";
    internal const string Host = "host";
    internal const string Path = "path";
    internal const string Port = "port";
    internal const string Scheme = "scheme";
    internal const string HttpHeaders = "httpHeaders";
    internal const string Name = "name";
    internal const string Value = "value";
}

/// <summary>
/// Nested keys under securityContext payload.
/// </summary>
internal static class KubernetesSecurityContextPayloadProperties
{
    internal const string Capabilities = "capabilities";
    internal const string Add = "add";
    internal const string Drop = "drop";
    internal const string SeLinuxOptions = "seLinuxOptions";
    internal const string Level = "level";
    internal const string Role = "role";
    internal const string Type = "type";
    internal const string User = "user";
}

/// <summary>
/// Keys for ingress rules/paths/backend/tls payload.
/// </summary>
internal static class KubernetesIngressPayloadProperties
{
    internal const string Host = "host";
    internal const string Http = "http";
    internal const string Paths = "paths";
    internal const string Path = "path";
    internal const string PathType = "pathType";
    internal const string Backend = "backend";
    internal const string ServiceName = "serviceName";
    internal const string ServicePort = "servicePort";
    internal const string Service = "service";
    internal const string Name = "name";
    internal const string Port = "port";
    internal const string Number = "number";
    internal const string SecretName = "secretName";
    internal const string Hosts = "hosts";
}

/// <summary>
/// Keys for hostAliases payload.
/// </summary>
internal static class KubernetesHostAliasPayloadProperties
{
    internal const string Ip = "ip";
    internal const string Hostnames = "hostnames";
}

/// <summary>
/// Keys for imagePullSecrets payload.
/// </summary>
internal static class KubernetesImagePullSecretPayloadProperties
{
    internal const string Name = "name";
}

internal static class KubernetesLabelKeys
{
    internal const string App = "app";
}

internal static class KubernetesBooleanValues
{
    internal const string True = "True";
    internal const string False = "False";
}

internal static class KubernetesJsonLiterals
{
    internal const string EmptyArray = "[]";
    internal const string EmptyObject = "{}";
}

internal static class KubernetesDeploymentStrategyValues
{
    internal const string Recreate = "Recreate";
    internal const string RollingUpdate = "RollingUpdate";
}

internal static class KubernetesPodDefaultValues
{
    internal const string RestartPolicyAlways = "Always";
    internal const string DnsPolicyClusterFirst = "ClusterFirst";
}

internal static class KubernetesServiceTypeValues
{
    internal const string ClusterIp = "ClusterIP";
    internal const string NodePort = "NodePort";
    internal const string LoadBalancer = "LoadBalancer";
}

internal static class KubernetesDefaultValues
{
    internal const string Namespace = "default";
    internal const string ContainerName = "container";
    internal const string ContainerImage = "nginx:latest";
    internal const string PortName = "http";
    internal const string ProtocolTcp = "TCP";
}

internal static class KubernetesIngressDefaultValues
{
    internal const string Name = "ingress";
    internal const string Path = "/";
    internal const string PathType = "Prefix";
}

/// <summary>
/// Keys for each item inside tolerations payload.
/// </summary>
internal static class KubernetesTolerationPayloadProperties
{
    internal const string Key = "key";
    internal const string Operator = "operator";
    internal const string Value = "value";
    internal const string Effect = "effect";
}

/// <summary>
/// Keys for each item inside pod security sysctls payload.
/// </summary>
internal static class KubernetesSysctlPayloadProperties
{
    internal const string Name = "name";
    internal const string Value = "value";
}

/// <summary>
/// Keys for each item inside dns config options payload.
/// </summary>
internal static class KubernetesDnsOptionPayloadProperties
{
    internal const string Name = "name";
    internal const string Value = "value";
}

/// <summary>
/// Keys for each item inside readiness gates payload.
/// </summary>
internal static class KubernetesReadinessGatePayloadProperties
{
    internal const string ConditionType = "conditionType";
}

internal static class KubernetesRawYamlProperties
{
    internal const string InlineYaml = "Squid.Action.KubernetesYaml.InlineYaml";
}

internal static class KubernetesHelmProperties
{
    internal const string ReleaseName = "Squid.Action.Helm.ReleaseName";
    internal const string ChartPath = "Squid.Action.Helm.ChartPath";
    internal const string CustomHelmExecutable = "Squid.Action.Helm.CustomHelmExecutable";
    internal const string ResetValues = "Squid.Action.Helm.ResetValues";
    internal const string ClientVersion = "Squid.Action.Helm.ClientVersion";
    internal const string AdditionalArgs = "Squid.Action.Helm.AdditionalArgs";
    internal const string YamlValues = "Squid.Action.Helm.YamlValues";
    internal const string KeyValues = "Squid.Action.Helm.KeyValues";
    internal const string HelmWait = "Squid.Action.Helm.Wait";
    internal const string WaitForJobs = "Squid.Action.Helm.WaitForJobs";
    internal const string Timeout = "Squid.Action.Helm.Timeout";
    internal const string ValueSources = "Squid.Action.Helm.ValueSources";
}

internal static class KubernetesKustomizeProperties
{
    internal const string OverlayPath = "Squid.Action.KubernetesKustomize.OverlayPath";
    internal const string CustomKustomizePath = "Squid.Action.KubernetesKustomize.CustomKustomizePath";
    internal const string AdditionalArgs = "Squid.Action.KubernetesKustomize.AdditionalArgs";
}
