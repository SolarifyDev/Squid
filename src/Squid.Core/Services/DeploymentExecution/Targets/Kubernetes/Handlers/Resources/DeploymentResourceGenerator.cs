using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class DeploymentResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        return KubernetesPropertyParser.ParseContainers(properties).Count > 0;
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var deploymentName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.DeploymentName");
        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var replicasText = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.Replicas");

        int replicas = 1;

        if (!string.IsNullOrWhiteSpace(replicasText) && int.TryParse(replicasText, out var parsed) && parsed > 0)
            replicas = parsed;

        var containerSpecs = KubernetesPropertyParser.ParseContainers(properties);

        if (containerSpecs.Count == 0)
            return string.Empty;

        var deploymentStrategy = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.DeploymentStyle");

        if (string.IsNullOrWhiteSpace(deploymentStrategy))
            deploymentStrategy = "RollingUpdate";

        var volumes = KubernetesPropertyParser.ParseVolumes(properties);

        var deploymentAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, "Squid.Action.KubernetesContainers.DeploymentAnnotations");
        var deploymentLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, "Squid.Action.KubernetesContainers.DeploymentLabels");
        var podAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, "Squid.Action.KubernetesContainers.PodAnnotations");

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {deploymentName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {namespaceName}");

        if (deploymentAnnotations.Count > 0)
        {
            sb.AppendLine("  annotations:");

            foreach (var kvp in deploymentAnnotations)
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "    ", kvp.Key, kvp.Value);
        }

        if (deploymentLabels.Count > 0)
        {
            sb.AppendLine("  labels:");

            foreach (var kvp in deploymentLabels)
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "    ", kvp.Key, kvp.Value);
        }

        var selectorLabels = deploymentLabels.Count > 0
            ? deploymentLabels
            : new Dictionary<string, string> { ["app"] = deploymentName };

        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {replicas}");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "      ", kvp.Key, kvp.Value);

        sb.AppendLine("  strategy:");
        sb.AppendLine($"    type: {deploymentStrategy}");
        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "        ", kvp.Key, kvp.Value);

        if (podAnnotations.Count > 0)
        {
            sb.AppendLine("      annotations:");

            foreach (var kvp in podAnnotations)
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "        ", kvp.Key, kvp.Value);
        }

        sb.AppendLine("    spec:");

        if (volumes.Count > 0)
        {
            sb.AppendLine("      volumes:");

            foreach (var volume in volumes)
            {
                sb.AppendLine($"      - name: {volume.Name}");

                if (!string.IsNullOrWhiteSpace(volume.ConfigMapName))
                {
                    sb.AppendLine("        configMap:");
                    sb.AppendLine($"          name: {volume.ConfigMapName}");
                }
            }
        }

        KubernetesPropertyParser.AppendJsonFromProperty(sb, "      ", "tolerations", properties, "Squid.Action.KubernetesContainers.Tolerations");

        AppendAffinityIfPresent(sb, properties);
        AppendDnsConfigIfPresent(sb, properties);
        AppendPodSecuritySysctlsIfPresent(sb, properties);
        AppendImagePullSecretsIfPresent(sb, properties);

        sb.AppendLine("      containers:");

        foreach (var container in containerSpecs)
            AppendContainerYaml(sb, container);

        return sb.ToString();
    }

    private static void AppendAffinityIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        var hasNodeAffinity = properties.TryGetValue("Squid.Action.KubernetesContainers.NodeAffinity", out var nodeAffinityRaw)
            && !string.IsNullOrWhiteSpace(nodeAffinityRaw)
            && !string.Equals(nodeAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(nodeAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        var hasPodAffinity = properties.TryGetValue("Squid.Action.KubernetesContainers.PodAffinity", out var podAffinityRaw)
            && !string.IsNullOrWhiteSpace(podAffinityRaw)
            && !string.Equals(podAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(podAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        var hasPodAntiAffinity = properties.TryGetValue("Squid.Action.KubernetesContainers.PodAntiAffinity", out var podAntiAffinityRaw)
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
        if (!properties.TryGetValue("Squid.Action.KubernetesContainers.DnsConfigOptions", out var dnsConfigRaw)
            || string.IsNullOrWhiteSpace(dnsConfigRaw))
        {
            return;
        }

        dnsConfigRaw = dnsConfigRaw.Trim();

        if (string.Equals(dnsConfigRaw, "[]", StringComparison.Ordinal) || string.Equals(dnsConfigRaw, "{}", StringComparison.Ordinal))
            return;

        try
        {
            using var doc = JsonDocument.Parse(dnsConfigRaw);
            sb.AppendLine("      dnsConfig:");
            sb.AppendLine("        options:");
            KubernetesPropertyParser.AppendJsonElementYaml(sb, "          ", doc.RootElement);
        }
        catch { }
    }

    private static void AppendPodSecuritySysctlsIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue("Squid.Action.KubernetesContainers.PodSecuritySysctls", out var raw)
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
            sb.AppendLine("      securityContext:");
            sb.AppendLine("        sysctls:");
            KubernetesPropertyParser.AppendJsonElementYaml(sb, "          ", doc.RootElement);
        }
        catch { }
    }

    private static void AppendImagePullSecretsIfPresent(StringBuilder sb, Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue("Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets", out var raw)
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
