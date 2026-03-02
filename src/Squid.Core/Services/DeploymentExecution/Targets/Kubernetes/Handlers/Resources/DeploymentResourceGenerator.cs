using System.Linq;
using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class DeploymentResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
        => KubernetesPropertyParser.ParseContainers(properties).Count > 0;

    public string Generate(Dictionary<string, string> properties)
    {
        var deploymentName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);
        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var replicasText = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.Replicas);

        int replicas = 1;

        if (!string.IsNullOrWhiteSpace(replicasText) && int.TryParse(replicasText, out var parsed) && parsed > 0)
            replicas = parsed;

        var containerSpecs = KubernetesPropertyParser.ParseContainers(properties);

        if (containerSpecs.Count == 0)
            return string.Empty;

        var deploymentStrategy = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentStyle);

        if (string.IsNullOrWhiteSpace(deploymentStrategy))
            deploymentStrategy = "RollingUpdate";

        var volumes = KubernetesPropertyParser.ParseVolumes(properties);
        var deploymentAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentAnnotations);
        var deploymentLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);
        var podAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.PodAnnotations);

        var selectorLabels = deploymentLabels.Count > 0
            ? deploymentLabels
            : new Dictionary<string, string> { ["app"] = deploymentName };

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {deploymentName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {namespaceName}");

        AppendDictionary(sb, "  annotations:", "    ", deploymentAnnotations);
        AppendDictionary(sb, "  labels:", "    ", deploymentLabels);

        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {replicas}");
        AppendIntPropertyIfPresent(sb, "  ", "revisionHistoryLimit", properties, KubernetesProperties.RevisionHistoryLimit);
        AppendIntPropertyIfPresent(sb, "  ", "progressDeadlineSeconds", properties, KubernetesProperties.ProgressDeadlineSeconds);

        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "      ", kvp.Key, kvp.Value);

        var k8sStrategyType = string.Equals(deploymentStrategy, "Recreate", StringComparison.OrdinalIgnoreCase)
            ? "Recreate"
            : "RollingUpdate";

        sb.AppendLine("  strategy:");
        sb.AppendLine($"    type: {k8sStrategyType}");
        AppendRollingUpdateIfPresent(sb, k8sStrategyType, properties);

        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "        ", kvp.Key, kvp.Value);

        AppendDictionary(sb, "      annotations:", "        ", podAnnotations);

        sb.AppendLine("    spec:");
        AppendStringPropertyIfPresent(sb, "      ", "serviceAccountName", properties, KubernetesProperties.ServiceAccountName);
        AppendRestartPolicyIfNeeded(sb, properties);
        AppendDnsPolicyIfNeeded(sb, properties);
        AppendHostNetworkIfNeeded(sb, properties);
        AppendIntPropertyIfPresent(sb, "      ", "terminationGracePeriodSeconds", properties, KubernetesProperties.PodTerminationGracePeriodSeconds);
        AppendStringPropertyIfPresent(sb, "      ", "priorityClassName", properties, KubernetesProperties.PodPriorityClassName);
        AppendReadinessGatesIfPresent(sb, properties);

        if (volumes.Count > 0)
        {
            sb.AppendLine("      volumes:");

            foreach (var volume in volumes)
                AppendVolumeYaml(sb, volume);
        }

        KubernetesPropertyParser.AppendJsonFromProperty(sb, "      ", "tolerations", properties, KubernetesProperties.Tolerations);

        AppendAffinityIfPresent(sb, properties);
        AppendDnsConfigIfPresent(sb, properties);
        AppendPodSecurityContextIfPresent(sb, properties);
        AppendImagePullSecretsIfPresent(sb, properties);
        AppendHostAliasesIfPresent(sb, properties);

        var initContainers = containerSpecs.Where(c => c.IsInitContainer).ToList();
        var regularContainers = containerSpecs.Where(c => !c.IsInitContainer).ToList();

        if (initContainers.Count > 0)
        {
            sb.AppendLine("      initContainers:");

            foreach (var container in initContainers)
                AppendContainerYaml(sb, container);
        }

        sb.AppendLine("      containers:");

        foreach (var container in regularContainers)
            AppendContainerYaml(sb, container);

        return sb.ToString();
    }

    private static void AppendVolumeYaml(StringBuilder sb, VolumeSpec volume)
    {
        sb.AppendLine($"      - name: {volume.Name}");

        if (!string.IsNullOrWhiteSpace(volume.ConfigMapName))
        {
            sb.AppendLine("        configMap:");
            sb.AppendLine($"          name: {volume.ConfigMapName}");
        }
        else if (!string.IsNullOrWhiteSpace(volume.SecretName))
        {
            sb.AppendLine("        secret:");
            sb.AppendLine($"          secretName: {volume.SecretName}");
        }
        else if (volume.EmptyDir)
        {
            sb.AppendLine("        emptyDir: {}");
        }
        else if (!string.IsNullOrWhiteSpace(volume.PvcClaimName))
        {
            sb.AppendLine("        persistentVolumeClaim:");
            sb.AppendLine($"          claimName: {volume.PvcClaimName}");
        }
        else if (!string.IsNullOrWhiteSpace(volume.HostPath))
        {
            sb.AppendLine("        hostPath:");
            sb.AppendLine($"          path: {volume.HostPath}");
        }
    }

    private static void AppendDictionary(StringBuilder sb, string header, string indent, Dictionary<string, string> dict)
    {
        if (dict.Count == 0)
            return;

        sb.AppendLine(header);

        foreach (var kvp in dict)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, indent, kvp.Key, kvp.Value);
    }

    private static void AppendIntPropertyIfPresent(StringBuilder sb, string indent, string yamlKey, Dictionary<string, string> properties, string propertyName)
    {
        var raw = KubernetesPropertyParser.GetProperty(properties, propertyName);

        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out _))
            sb.AppendLine($"{indent}{yamlKey}: {raw}");
    }

    private static void AppendStringPropertyIfPresent(StringBuilder sb, string indent, string yamlKey, Dictionary<string, string> properties, string propertyName)
    {
        var value = KubernetesPropertyParser.GetProperty(properties, propertyName);
        KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, indent, yamlKey, value);
    }

    private static void AppendRollingUpdateIfPresent(StringBuilder sb, string deploymentStrategy, Dictionary<string, string> properties)
    {
        if (!string.Equals(deploymentStrategy, "RollingUpdate", StringComparison.OrdinalIgnoreCase))
            return;

        var maxUnavailable = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.MaxUnavailable);
        var maxSurge = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.MaxSurge);

        if (string.IsNullOrWhiteSpace(maxUnavailable) && string.IsNullOrWhiteSpace(maxSurge))
            return;

        sb.AppendLine("    rollingUpdate:");
        KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "      ", "maxUnavailable", maxUnavailable);
        KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "      ", "maxSurge", maxSurge);
    }

    private static void AppendRestartPolicyIfNeeded(StringBuilder sb, Dictionary<string, string> properties)
    {
        var restartPolicy = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodRestartPolicy);

        if (string.IsNullOrWhiteSpace(restartPolicy) || string.Equals(restartPolicy, "Always", StringComparison.OrdinalIgnoreCase))
            return;

        sb.AppendLine($"      restartPolicy: {restartPolicy}");
    }

    private static void AppendDnsPolicyIfNeeded(StringBuilder sb, Dictionary<string, string> properties)
    {
        var dnsPolicy = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodDnsPolicy);

        if (string.IsNullOrWhiteSpace(dnsPolicy) || string.Equals(dnsPolicy, "ClusterFirst", StringComparison.OrdinalIgnoreCase))
            return;

        sb.AppendLine($"      dnsPolicy: {dnsPolicy}");
    }

    private static void AppendHostNetworkIfNeeded(StringBuilder sb, Dictionary<string, string> properties)
    {
        var raw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodHostNetworking);

        if (string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("      hostNetwork: true");
    }

    private static void AppendReadinessGatesIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        var raw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodReadinessGates);

        if (string.IsNullOrWhiteSpace(raw))
            return;

        var gates = raw.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (gates.Length == 0)
            return;

        sb.AppendLine("      readinessGates:");

        foreach (var gate in gates)
            sb.AppendLine($"      - conditionType: {gate}");
    }

    private static void AppendAffinityIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        var hasNodeAffinity = properties.TryGetValue(KubernetesProperties.NodeAffinity, out var nodeAffinityRaw)
            && !string.IsNullOrWhiteSpace(nodeAffinityRaw)
            && !string.Equals(nodeAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(nodeAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        var hasPodAffinity = properties.TryGetValue(KubernetesProperties.PodAffinity, out var podAffinityRaw)
            && !string.IsNullOrWhiteSpace(podAffinityRaw)
            && !string.Equals(podAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(podAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        var hasPodAntiAffinity = properties.TryGetValue(KubernetesProperties.PodAntiAffinity, out var podAntiAffinityRaw)
            && !string.IsNullOrWhiteSpace(podAntiAffinityRaw)
            && !string.Equals(podAntiAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(podAntiAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        if (!hasNodeAffinity && !hasPodAffinity && !hasPodAntiAffinity)
            return;

        sb.AppendLine("      affinity:");

        if (hasNodeAffinity)
        {
            try
            {
                using var doc = JsonDocument.Parse(nodeAffinityRaw!);
                sb.AppendLine("        nodeAffinity:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, "          ", doc.RootElement);
            }
            catch { }
        }

        if (hasPodAffinity)
        {
            try
            {
                using var doc = JsonDocument.Parse(podAffinityRaw!);
                sb.AppendLine("        podAffinity:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, "          ", doc.RootElement);
            }
            catch { }
        }

        if (hasPodAntiAffinity)
        {
            try
            {
                using var doc = JsonDocument.Parse(podAntiAffinityRaw!);
                sb.AppendLine("        podAntiAffinity:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, "          ", doc.RootElement);
            }
            catch { }
        }
    }

    private static void AppendDnsConfigIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        var optionsRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DnsConfigOptions).Trim();
        var nameserversRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodDnsNameservers);
        var searchesRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodDnsSearches);

        var hasOptions = !string.IsNullOrWhiteSpace(optionsRaw)
            && !string.Equals(optionsRaw, "[]", StringComparison.Ordinal)
            && !string.Equals(optionsRaw, "{}", StringComparison.Ordinal);

        var nameservers = SplitCommaSeparated(nameserversRaw);
        var searches = SplitCommaSeparated(searchesRaw);

        if (!hasOptions && nameservers.Length == 0 && searches.Length == 0)
            return;

        sb.AppendLine("      dnsConfig:");

        if (nameservers.Length > 0)
        {
            sb.AppendLine("        nameservers:");

            foreach (var ns in nameservers)
                sb.AppendLine($"        - {ns}");
        }

        if (searches.Length > 0)
        {
            sb.AppendLine("        searches:");

            foreach (var s in searches)
                sb.AppendLine($"        - {s}");
        }

        if (hasOptions)
        {
            try
            {
                using var doc = JsonDocument.Parse(optionsRaw);
                sb.AppendLine("        options:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, "          ", doc.RootElement);
            }
            catch { }
        }
    }

    private static void AppendPodSecurityContextIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        var sysctlsRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecuritySysctls).Trim();
        var fsGroup = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecurityFsGroup);
        var runAsUser = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecurityRunAsUser);
        var runAsGroup = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecurityRunAsGroup);
        var runAsNonRoot = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecurityRunAsNonRoot);
        var supplementalGroupsRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecuritySupplementalGroups);
        var seLinuxLevel = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecuritySeLinuxLevel);
        var seLinuxRole = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecuritySeLinuxRole);
        var seLinuxType = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecuritySeLinuxType);
        var seLinuxUser = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodSecuritySeLinuxUser);

        var hasSysctls = !string.IsNullOrWhiteSpace(sysctlsRaw)
            && !string.Equals(sysctlsRaw, "[]", StringComparison.Ordinal)
            && !string.Equals(sysctlsRaw, "{}", StringComparison.Ordinal);

        var supplementalGroups = SplitCommaSeparated(supplementalGroupsRaw);
        var hasRunAsNonRoot = string.Equals(runAsNonRoot, "True", StringComparison.OrdinalIgnoreCase);
        var hasSeLinux = !string.IsNullOrWhiteSpace(seLinuxLevel) || !string.IsNullOrWhiteSpace(seLinuxRole)
            || !string.IsNullOrWhiteSpace(seLinuxType) || !string.IsNullOrWhiteSpace(seLinuxUser);

        var hasAnything = hasSysctls
            || !string.IsNullOrWhiteSpace(fsGroup)
            || !string.IsNullOrWhiteSpace(runAsUser)
            || !string.IsNullOrWhiteSpace(runAsGroup)
            || hasRunAsNonRoot
            || supplementalGroups.Length > 0
            || hasSeLinux;

        if (!hasAnything)
            return;

        sb.AppendLine("      securityContext:");
        AppendIntValueIfPresent(sb, "        ", "fsGroup", fsGroup);
        AppendIntValueIfPresent(sb, "        ", "runAsUser", runAsUser);
        AppendIntValueIfPresent(sb, "        ", "runAsGroup", runAsGroup);

        if (hasRunAsNonRoot)
            sb.AppendLine("        runAsNonRoot: true");

        if (supplementalGroups.Length > 0)
        {
            sb.AppendLine("        supplementalGroups:");

            foreach (var g in supplementalGroups)
            {
                if (int.TryParse(g, out _))
                    sb.AppendLine($"        - {g}");
            }
        }

        if (hasSeLinux)
        {
            sb.AppendLine("        seLinuxOptions:");
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "level", seLinuxLevel);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "role", seLinuxRole);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "type", seLinuxType);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "user", seLinuxUser);
        }

        if (hasSysctls)
        {
            try
            {
                using var doc = JsonDocument.Parse(sysctlsRaw);
                sb.AppendLine("        sysctls:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, "          ", doc.RootElement);
            }
            catch { }
        }
    }

    private static void AppendImagePullSecretsIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue(KubernetesProperties.PodSecurityImagePullSecrets, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        raw = raw.Trim();

        if (string.Equals(raw, "[]", StringComparison.Ordinal) || string.Equals(raw, "{}", StringComparison.Ordinal))
            return;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            sb.AppendLine("      imagePullSecrets:");

            foreach (var secret in doc.RootElement.EnumerateArray())
            {
                if (secret.TryGetProperty("name", out var nameElement))
                    sb.AppendLine($"      - name: {nameElement.GetString()}");
            }
        }
        catch { }
    }

    private static void AppendHostAliasesIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue(KubernetesProperties.HostAliases, out var raw)
            || string.IsNullOrWhiteSpace(raw))
            return;

        raw = raw.Trim();

        if (string.Equals(raw, "[]", StringComparison.Ordinal) || string.Equals(raw, "{}", StringComparison.Ordinal))
            return;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            var hasAny = false;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var ip = element.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(ip))
                    continue;

                if (!hasAny)
                {
                    sb.AppendLine("      hostAliases:");
                    hasAny = true;
                }

                sb.AppendLine($"      - ip: {ip}");

                var hostnames = ParseHostnames(element);

                if (hostnames.Length > 0)
                {
                    sb.AppendLine("        hostnames:");

                    foreach (var hostname in hostnames)
                        sb.AppendLine($"        - {hostname}");
                }
            }
        }
        catch { }
    }

    private static string[] ParseHostnames(JsonElement element)
    {
        if (!element.TryGetProperty("hostnames", out var hostnamesProp))
            return [];

        if (hostnamesProp.ValueKind == JsonValueKind.String)
        {
            return (hostnamesProp.GetString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (hostnamesProp.ValueKind == JsonValueKind.Array)
        {
            return hostnamesProp.EnumerateArray()
                .Select(h => h.GetString() ?? string.Empty)
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToArray();
        }

        return [];
    }

    private static void AppendIntValueIfPresent(StringBuilder sb, string indent, string yamlKey, string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out _))
            sb.AppendLine($"{indent}{yamlKey}: {raw}");
    }

    private static string[] SplitCommaSeparated(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void AppendContainerYaml(StringBuilder sb, ContainerSpec container)
    {
        sb.AppendLine($"      - name: {container.Name}");
        sb.AppendLine($"        image: {container.Image}");

        if (container.Ports.Count > 0)
        {
            sb.AppendLine("        ports:");

            foreach (var port in container.Ports)
            {
                sb.AppendLine($"        - name: {port.Name}");
                sb.AppendLine($"          containerPort: {port.Port}");

                if (!string.IsNullOrWhiteSpace(port.Protocol))
                    sb.AppendLine($"          protocol: {port.Protocol}");
            }
        }

        if (container.ResourcesRequests.Count > 0 || container.ResourcesLimits.Count > 0)
        {
            sb.AppendLine("        resources:");

            if (container.ResourcesRequests.Count > 0)
            {
                sb.AppendLine("          requests:");

                foreach (var kvp in container.ResourcesRequests)
                    sb.AppendLine($"            {kvp.Key}: {kvp.Value}");
            }

            if (container.ResourcesLimits.Count > 0)
            {
                sb.AppendLine("          limits:");

                foreach (var kvp in container.ResourcesLimits)
                    sb.AppendLine($"            {kvp.Key}: {kvp.Value}");
            }
        }

        if (container.VolumeMounts.Count > 0)
        {
            sb.AppendLine("        volumeMounts:");

            foreach (var mount in container.VolumeMounts)
            {
                sb.AppendLine($"        - name: {mount.Name}");
                sb.AppendLine($"          mountPath: {mount.MountPath}");

                if (!string.IsNullOrWhiteSpace(mount.SubPath))
                    sb.AppendLine($"          subPath: {mount.SubPath}");
            }
        }

        if (container.ConfigMapEnvFromSource.Count > 0)
        {
            sb.AppendLine("        envFrom:");

            foreach (var envFrom in container.ConfigMapEnvFromSource)
            {
                sb.AppendLine("        - configMapRef:");
                sb.AppendLine($"            name: {envFrom}");
            }
        }

        if (container.LivenessProbe != null)
            KubernetesPropertyParser.AppendProbeYaml(sb, "        ", "livenessProbe", container.LivenessProbe);

        if (container.ReadinessProbe != null)
            KubernetesPropertyParser.AppendProbeYaml(sb, "        ", "readinessProbe", container.ReadinessProbe);

        if (container.StartupProbe != null)
            KubernetesPropertyParser.AppendProbeYaml(sb, "        ", "startupProbe", container.StartupProbe);

        if (container.Lifecycle != null)
        {
            sb.AppendLine("        lifecycle:");

            if (container.Lifecycle.PreStop != null)
                KubernetesPropertyParser.AppendLifecycleHandlerYaml(sb, "          ", "preStop", container.Lifecycle.PreStop);

            if (container.Lifecycle.PostStart != null)
                KubernetesPropertyParser.AppendLifecycleHandlerYaml(sb, "          ", "postStart", container.Lifecycle.PostStart);
        }

        if (container.SecurityContext != null)
        {
            sb.AppendLine("        securityContext:");

            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "allowPrivilegeEscalation", container.SecurityContext.AllowPrivilegeEscalation);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "privileged", container.SecurityContext.Privileged);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "readOnlyRootFilesystem", container.SecurityContext.ReadOnlyRootFilesystem);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "runAsGroup", container.SecurityContext.RunAsGroup);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "runAsNonRoot", container.SecurityContext.RunAsNonRoot);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "runAsUser", container.SecurityContext.RunAsUser);

            if (container.SecurityContext.Capabilities != null
                && (container.SecurityContext.Capabilities.Add.Count > 0 || container.SecurityContext.Capabilities.Drop.Count > 0))
            {
                sb.AppendLine("          capabilities:");

                if (container.SecurityContext.Capabilities.Add.Count > 0)
                {
                    sb.AppendLine("            add:");

                    foreach (var capability in container.SecurityContext.Capabilities.Add)
                    {
                        if (!string.IsNullOrWhiteSpace(capability))
                            sb.AppendLine($"            - {capability}");
                    }
                }

                if (container.SecurityContext.Capabilities.Drop.Count > 0)
                {
                    sb.AppendLine("            drop:");

                    foreach (var capability in container.SecurityContext.Capabilities.Drop)
                    {
                        if (!string.IsNullOrWhiteSpace(capability))
                            sb.AppendLine($"            - {capability}");
                    }
                }
            }

            if (container.SecurityContext.SeLinuxOptions != null)
            {
                sb.AppendLine("          seLinuxOptions:");

                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "level", container.SecurityContext.SeLinuxOptions.Level);
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "role", container.SecurityContext.SeLinuxOptions.Role);
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "type", container.SecurityContext.SeLinuxOptions.Type);
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "user", container.SecurityContext.SeLinuxOptions.User);
            }
        }
    }
}
