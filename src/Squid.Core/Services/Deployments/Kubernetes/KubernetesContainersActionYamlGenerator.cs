using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesContainersActionYamlGenerator : IActionYamlGenerator
{
    private const string ContainersActionType = "Octopus.KubernetesDeployContainers";

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null)
        {
            return false;
        }

        return string.Equals(action.ActionType, ContainersActionType, StringComparison.OrdinalIgnoreCase);
    }

    public Task<Dictionary<string, byte[]>> GenerateAsync(
        DeploymentStepDto step,
        DeploymentActionDto action,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>();

        if (!CanHandle(action))
        {
            return Task.FromResult(result);
        }

        var properties = BuildPropertyDictionary(action);

        cancellationToken.ThrowIfCancellationRequested();

        var deploymentYaml = GenerateDeploymentYaml(action, properties);

        if (!string.IsNullOrWhiteSpace(deploymentYaml))
        {
            result["deployment.yaml"] = Encoding.UTF8.GetBytes(deploymentYaml);
        }

        var serviceYaml = GenerateServiceYaml(action, properties);

        if (!string.IsNullOrWhiteSpace(serviceYaml))
        {
            result["service.yaml"] = Encoding.UTF8.GetBytes(serviceYaml);
        }

        var configMapYaml = GenerateConfigMapYaml(action, properties);

        if (!string.IsNullOrWhiteSpace(configMapYaml))
        {
            result["configmap.yaml"] = Encoding.UTF8.GetBytes(configMapYaml);
        }

        return Task.FromResult(result);
    }

    private static Dictionary<string, string> BuildPropertyDictionary(DeploymentActionDto action)
    {
        if (action.Properties == null || action.Properties.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var dict = new Dictionary<string, string>(action.Properties.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var prop in action.Properties)
        {
            dict[prop.PropertyName] = prop.PropertyValue;
        }

        return dict;
    }

    private string GenerateDeploymentYaml(DeploymentActionDto action, Dictionary<string, string> properties)
    {
        var deploymentName = GetProperty(properties, "Octopus.Action.KubernetesContainers.DeploymentName");

        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            deploymentName = action.Name;
        }

        var namespaceName = GetNamespace(properties);

        var replicasText = GetProperty(properties, "Octopus.Action.KubernetesContainers.Replicas");

        int replicas = 1;

        if (!string.IsNullOrWhiteSpace(replicasText) && int.TryParse(replicasText, out var parsed) && parsed > 0)
        {
            replicas = parsed;
        }

        var containerSpecs = ParseContainers(properties);

        if (containerSpecs.Count == 0)
        {
            return string.Empty;
        }

        var deploymentStrategy = GetProperty(properties, "Octopus.Action.KubernetesContainers.DeploymentStyle");

        if (string.IsNullOrWhiteSpace(deploymentStrategy))
        {
            deploymentStrategy = "RollingUpdate";
        }

        var volumes = ParseVolumes(properties);

        var deploymentAnnotations = ParseStringDictionaryProperty(
            properties,
            "Octopus.Action.KubernetesContainers.DeploymentAnnotations");

        var deploymentLabels = ParseStringDictionaryProperty(
            properties,
            "Octopus.Action.KubernetesContainers.DeploymentLabels");

        var podAnnotations = ParseStringDictionaryProperty(
            properties,
            "Octopus.Action.KubernetesContainers.PodAnnotations");

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {deploymentName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            sb.AppendLine($"  namespace: {namespaceName}");
        }

        if (deploymentAnnotations.Count > 0)
        {
            sb.AppendLine("  annotations:");

            foreach (var kvp in deploymentAnnotations)
            {
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "    ", kvp.Key, kvp.Value);
            }
        }

        if (deploymentLabels.Count > 0)
        {
            sb.AppendLine("  labels:");

            foreach (var kvp in deploymentLabels)
            {
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "    ", kvp.Key, kvp.Value);
            }
        }

        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {replicas}");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.AppendLine($"      app: {deploymentName}");
        sb.AppendLine("  strategy:");
        sb.AppendLine($"    type: {deploymentStrategy}");
        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");
        sb.AppendLine($"        app: {deploymentName}");

        if (podAnnotations.Count > 0)
        {
            sb.AppendLine("      annotations:");

            foreach (var kvp in podAnnotations)
            {
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "        ", kvp.Key, kvp.Value);
            }
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

        AppendJsonFromProperty(
            sb,
            "      ",
            "tolerations",
            properties,
            "Octopus.Action.KubernetesContainers.Tolerations");

        var hasNodeAffinity = properties.TryGetValue("Octopus.Action.KubernetesContainers.NodeAffinity", out var nodeAffinityRaw)
            && !string.IsNullOrWhiteSpace(nodeAffinityRaw)
            && !string.Equals(nodeAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(nodeAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        var hasPodAffinity = properties.TryGetValue("Octopus.Action.KubernetesContainers.PodAffinity", out var podAffinityRaw)
            && !string.IsNullOrWhiteSpace(podAffinityRaw)
            && !string.Equals(podAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(podAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        var hasPodAntiAffinity = properties.TryGetValue("Octopus.Action.KubernetesContainers.PodAntiAffinity", out var podAntiAffinityRaw)
            && !string.IsNullOrWhiteSpace(podAntiAffinityRaw)
            && !string.Equals(podAntiAffinityRaw.Trim(), "[]", StringComparison.Ordinal)
            && !string.Equals(podAntiAffinityRaw.Trim(), "{}", StringComparison.Ordinal);

        if (hasNodeAffinity || hasPodAffinity || hasPodAntiAffinity)
        {
            sb.AppendLine("      affinity:");

            if (hasNodeAffinity)
            {
                try
                {
                    using var nodeAffinityDoc = JsonDocument.Parse(nodeAffinityRaw!);

                    sb.AppendLine("        nodeAffinity:");

                    AppendJsonElementYaml(sb, "          ", nodeAffinityDoc.RootElement);
                }
                catch
                {
                }
            }

            if (hasPodAffinity)
            {
                try
                {
                    using var podAffinityDoc = JsonDocument.Parse(podAffinityRaw!);

                    sb.AppendLine("        podAffinity:");

                    AppendJsonElementYaml(sb, "          ", podAffinityDoc.RootElement);
                }
                catch
                {
                }
            }

            if (hasPodAntiAffinity)
            {
                try
                {
                    using var podAntiAffinityDoc = JsonDocument.Parse(podAntiAffinityRaw!);

                    sb.AppendLine("        podAntiAffinity:");

                    AppendJsonElementYaml(sb, "          ", podAntiAffinityDoc.RootElement);
                }
                catch
                {
                }
            }
        }

        if (properties.TryGetValue("Octopus.Action.KubernetesContainers.DnsConfigOptions", out var dnsConfigRaw)
            && !string.IsNullOrWhiteSpace(dnsConfigRaw))
        {
            dnsConfigRaw = dnsConfigRaw.Trim();

            if (!string.Equals(dnsConfigRaw, "[]", StringComparison.Ordinal)
                && !string.Equals(dnsConfigRaw, "{}", StringComparison.Ordinal))
            {
                try
                {
                    using var dnsConfigDoc = JsonDocument.Parse(dnsConfigRaw);

                    sb.AppendLine("      dnsConfig:");
                    sb.AppendLine("        options:");

                    AppendJsonElementYaml(sb, "          ", dnsConfigDoc.RootElement);
                }
                catch
                {
                }
            }
        }

        if (properties.TryGetValue("Octopus.Action.KubernetesContainers.PodSecuritySysctls", out var podSecuritySysctlsRaw)
            && !string.IsNullOrWhiteSpace(podSecuritySysctlsRaw))
        {
            podSecuritySysctlsRaw = podSecuritySysctlsRaw.Trim();

            if (!string.Equals(podSecuritySysctlsRaw, "[]", StringComparison.Ordinal)
                && !string.Equals(podSecuritySysctlsRaw, "{}", StringComparison.Ordinal))
            {
                try
                {
                    using var podSecuritySysctlsDoc = JsonDocument.Parse(podSecuritySysctlsRaw);

                    sb.AppendLine("      securityContext:");
                    sb.AppendLine("        sysctls:");

                    AppendJsonElementYaml(sb, "          ", podSecuritySysctlsDoc.RootElement);
                }
                catch
                {
                }
            }
        }

        sb.AppendLine("      containers:");

        foreach (var container in containerSpecs)
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
                    {
                        sb.AppendLine($"          protocol: {port.Protocol}");
                    }
                }
            }

            if (container.ResourcesRequests.Count > 0 || container.ResourcesLimits.Count > 0)
            {
                sb.AppendLine("        resources:");

                if (container.ResourcesRequests.Count > 0)
                {
                    sb.AppendLine("          requests:");

                    foreach (var kvp in container.ResourcesRequests)
                    {
                        sb.AppendLine($"            {kvp.Key}: {kvp.Value}");
                    }
                }

                if (container.ResourcesLimits.Count > 0)
                {
                    sb.AppendLine("          limits:");

                    foreach (var kvp in container.ResourcesLimits)
                    {
                        sb.AppendLine($"            {kvp.Key}: {kvp.Value}");
                    }
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
                    {
                        sb.AppendLine($"          subPath: {mount.SubPath}");
                    }
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
            {
                AppendProbeYaml(sb, "        ", "livenessProbe", container.LivenessProbe);
            }

            if (container.ReadinessProbe != null)
            {
                AppendProbeYaml(sb, "        ", "readinessProbe", container.ReadinessProbe);
            }

            if (container.StartupProbe != null)
            {
                AppendProbeYaml(sb, "        ", "startupProbe", container.StartupProbe);
            }

            if (container.Lifecycle != null)
            {
                sb.AppendLine("        lifecycle:");

                if (container.Lifecycle.PreStop != null)
                {
                    AppendLifecycleHandlerYaml(sb, "          ", "preStop", container.Lifecycle.PreStop);
                }

                if (container.Lifecycle.PostStart != null)
                {
                    AppendLifecycleHandlerYaml(sb, "          ", "postStart", container.Lifecycle.PostStart);
                }
            }

            if (container.SecurityContext != null)
            {
                sb.AppendLine("        securityContext:");

                AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "allowPrivilegeEscalation", container.SecurityContext.AllowPrivilegeEscalation);
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "privileged", container.SecurityContext.Privileged);
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "readOnlyRootFilesystem", container.SecurityContext.ReadOnlyRootFilesystem);
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "runAsGroup", container.SecurityContext.RunAsGroup);
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "runAsNonRoot", container.SecurityContext.RunAsNonRoot);
                AppendKeyValueIfNotNullOrWhiteSpace(sb, "          ", "runAsUser", container.SecurityContext.RunAsUser);

                if (container.SecurityContext.Capabilities != null
                    && (container.SecurityContext.Capabilities.Add.Count > 0 || container.SecurityContext.Capabilities.Drop.Count > 0))
                {
                    sb.AppendLine("          capabilities:");

                    if (container.SecurityContext.Capabilities.Add.Count > 0)
                    {
                        sb.AppendLine("            add:");

                        foreach (var capability in container.SecurityContext.Capabilities.Add)
                        {
                            if (string.IsNullOrWhiteSpace(capability))
                            {
                                continue;
                            }

                            sb.AppendLine($"            - {capability}");
                        }
                    }

                    if (container.SecurityContext.Capabilities.Drop.Count > 0)
                    {
                        sb.AppendLine("            drop:");

                        foreach (var capability in container.SecurityContext.Capabilities.Drop)
                        {
                            if (string.IsNullOrWhiteSpace(capability))
                            {
                                continue;
                            }

                            sb.AppendLine($"            - {capability}");
                        }
                    }
                }

                if (container.SecurityContext.SeLinuxOptions != null)
                {
                    sb.AppendLine("          seLinuxOptions:");

                    AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "level", container.SecurityContext.SeLinuxOptions.Level);
                    AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "role", container.SecurityContext.SeLinuxOptions.Role);
                    AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "type", container.SecurityContext.SeLinuxOptions.Type);
                    AppendKeyValueIfNotNullOrWhiteSpace(sb, "            ", "user", container.SecurityContext.SeLinuxOptions.User);
                }
            }
        }

        return sb.ToString();
    }

    private string GenerateServiceYaml(DeploymentActionDto action, Dictionary<string, string> properties)
    {
        var serviceName = GetProperty(properties, "Octopus.Action.KubernetesContainers.ServiceName");

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return string.Empty;
        }

        var namespaceName = GetNamespace(properties);

        var deploymentName = GetProperty(properties, "Octopus.Action.KubernetesContainers.DeploymentName");

        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            deploymentName = action.Name;
        }

        var serviceType = GetProperty(properties, "Octopus.Action.KubernetesContainers.ServiceType");

        if (string.IsNullOrWhiteSpace(serviceType))
        {
            serviceType = "ClusterIP";
        }

        var ports = ParseServicePorts(properties);

        if (ports.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {serviceName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            sb.AppendLine($"  namespace: {namespaceName}");
        }

        sb.AppendLine("spec:");
        sb.AppendLine($"  type: {serviceType}");
        sb.AppendLine("  selector:");
        sb.AppendLine($"    app: {deploymentName}");
        sb.AppendLine("  ports:");

        foreach (var port in ports)
        {
            sb.AppendLine("  - name: " + port.Name);
            sb.AppendLine($"    port: {port.Port}");

            if (port.TargetPort.HasValue)
            {
                sb.AppendLine($"    targetPort: {port.TargetPort.Value}");
            }

            if (port.NodePort.HasValue)
            {
                sb.AppendLine($"    nodePort: {port.NodePort.Value}");
            }

            if (!string.IsNullOrWhiteSpace(port.Protocol))
            {
                sb.AppendLine($"    protocol: {port.Protocol}");
            }
        }

        return sb.ToString();
    }

    private string GenerateConfigMapYaml(DeploymentActionDto action, Dictionary<string, string> properties)
    {
        var configMapName = GetProperty(properties, "Octopus.Action.KubernetesContainers.ConfigMapName");

        var configValues = GetProperty(properties, "Octopus.Action.KubernetesContainers.ConfigMapValues");

        if (string.IsNullOrWhiteSpace(configMapName) || string.IsNullOrWhiteSpace(configValues))
        {
            return string.Empty;
        }

        var namespaceName = GetNamespace(properties);

        Dictionary<string, string> values;

        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(configValues) ?? new Dictionary<string, string>();
        }
        catch
        {
            values = new Dictionary<string, string>();
        }

        if (values.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ConfigMap");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {configMapName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
        {
            sb.AppendLine($"  namespace: {namespaceName}");
        }

        sb.AppendLine("data:");

        foreach (var kvp in values)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                sb.AppendLine($"  {kvp.Key}: \"\"");

                continue;
            }

            if (kvp.Value.Contains('\n', StringComparison.Ordinal))
            {
                var indented = kvp.Value.Replace("\r\n", "\n", StringComparison.Ordinal);
                indented = indented.Replace("\n", "\n    ", StringComparison.Ordinal);

                sb.AppendLine($"  {kvp.Key}: |");
                sb.AppendLine("    " + indented);
            }
            else
            {
                sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
            }
        }

        return sb.ToString();
    }

    private static string GetNamespace(Dictionary<string, string> properties)
    {
        var ns = GetProperty(properties, "Octopus.Action.KubernetesContainers.Namespace");

        if (string.IsNullOrWhiteSpace(ns))
        {
            ns = GetProperty(properties, "Octopus.Action.Kubernetes.Namespace");
        }

        if (string.IsNullOrWhiteSpace(ns))
        {
            ns = "default";
        }

        return ns;
    }

    private static string GetProperty(Dictionary<string, string> properties, string name)
    {
        if (properties.TryGetValue(name, out var value))
        {
            return value;
        }

        return string.Empty;
    }

    private static Dictionary<string, string> ParseStringDictionaryProperty(
        Dictionary<string, string> properties,
        string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!properties.TryGetValue(propertyName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var key = element.TryGetProperty("Key", out var keyProp) ? keyProp.GetString()
                        : element.TryGetProperty("key", out var lowerKeyProp) ? lowerKeyProp.GetString()
                        : null;

                    var value = element.TryGetProperty("Value", out var valueProp) ? valueProp.GetString()
                        : element.TryGetProperty("value", out var lowerValueProp) ? lowerValueProp.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(key) || value == null)
                    {
                        continue;
                    }

                    result[key] = value;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    var key = property.Name;

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var value = property.Value.GetString() ?? string.Empty;

                    result[key] = value;
                }
            }
        }
        catch
        {
        }

        return result;
    }


    private static List<ServicePortSpec> ParseServicePorts(Dictionary<string, string> properties)
    {
        var result = new List<ServicePortSpec>();

        var portsJson = GetProperty(properties, "Octopus.Action.KubernetesContainers.ServicePorts");

        if (string.IsNullOrWhiteSpace(portsJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(portsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var portText = element.TryGetProperty("port", out var portProp) ? portProp.GetString() : null;
                var targetPortText = element.TryGetProperty("targetPort", out var targetPortProp) ? targetPortProp.GetString() : null;
                var nodePortText = element.TryGetProperty("nodePort", out var nodePortProp) ? nodePortProp.GetString() : null;
                var protocol = element.TryGetProperty("protocol", out var protocolProp) ? protocolProp.GetString() ?? string.Empty : string.Empty;

                if (!int.TryParse(portText, out var port))
                {
                    continue;
                }

                int? targetPort = null;

                if (int.TryParse(targetPortText, out var parsedTargetPort))
                {
                    targetPort = parsedTargetPort;
                }

                int? nodePort = null;

                if (int.TryParse(nodePortText, out var parsedNodePort))
                {
                    nodePort = parsedNodePort;
                }

                result.Add(new ServicePortSpec
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "http" : name,
                    Port = port,
                    TargetPort = targetPort,
                    NodePort = nodePort,
                    Protocol = string.IsNullOrWhiteSpace(protocol) ? "TCP" : protocol
                });
            }
        }
        catch
        {
        }

        return result;
    }

    private static List<ContainerSpec> ParseContainers(Dictionary<string, string> properties)
    {
        var result = new List<ContainerSpec>();

        var containersJson = GetProperty(properties, "Octopus.Action.KubernetesContainers.Containers");

        if (string.IsNullOrWhiteSpace(containersJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(containersJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var image = GetFirstImagePropertyFromContainer(element) ?? "nginx:latest";

                var container = new ContainerSpec
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "container" : name,
                    Image = image
                };

                FillContainerPorts(element, container);
                FillContainerResources(element, container);
                FillContainerVolumeMounts(element, container);
                FillContainerConfigMapEnvFrom(element, container);
                FillContainerProbes(element, container);
                FillContainerSecurityContext(element, container);
                FillContainerLifecycle(element, container);

                result.Add(container);
            }
        }
        catch
        {
        }

        return result;
    }

    private static List<VolumeSpec> ParseVolumes(Dictionary<string, string> properties)
    {
        var result = new List<VolumeSpec>();

        var combinedVolumesJson = GetProperty(properties, "Octopus.Action.KubernetesContainers.CombinedVolumes");

        if (string.IsNullOrWhiteSpace(combinedVolumesJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(combinedVolumesJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var type = element.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
                var referenceName = element.TryGetProperty("ReferenceName", out var referenceProp) ? referenceProp.GetString() ?? string.Empty : string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var volume = new VolumeSpec
                {
                    Name = name
                };

                if (string.Equals(type, "ConfigMap", StringComparison.OrdinalIgnoreCase))
                {
                    volume.ConfigMapName = referenceName;
                }

                result.Add(volume);
            }
        }
        catch
        {
        }

        return result;
    }

    private static string GetFirstImagePropertyFromContainer(JsonElement containerElement)
    {
        if (containerElement.TryGetProperty("Image", out var imageProp))
        {
            return imageProp.GetString() ?? string.Empty;
        }

        if (containerElement.TryGetProperty("PackageId", out var packageIdProp))
        {
            var packageId = packageIdProp.GetString();

            if (!string.IsNullOrWhiteSpace(packageId))
            {
                return packageId;
            }
        }

        return null;
    }

    private static void FillContainerPorts(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("Ports", out var portsElement) || portsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var portElement in portsElement.EnumerateArray())
        {
            var name = portElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var portText = portElement.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
            var protocol = portElement.TryGetProperty("option", out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;

            if (!int.TryParse(portText, out var port))
            {
                continue;
            }

            container.Ports.Add(new ContainerPortSpec
            {
                Name = string.IsNullOrWhiteSpace(name) ? "http" : name,
                Port = port,
                Protocol = string.IsNullOrWhiteSpace(protocol) ? "TCP" : protocol
            });
        }
    }

    private static void FillContainerResources(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("Resources", out var resourcesElement) || resourcesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (resourcesElement.TryGetProperty("requests", out var requestsElement) && requestsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in requestsElement.EnumerateObject())
            {
                var value = property.Value.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    container.ResourcesRequests[property.Name] = value;
                }
            }
        }

        if (resourcesElement.TryGetProperty("limits", out var limitsElement) && limitsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in limitsElement.EnumerateObject())
            {
                var value = property.Value.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    container.ResourcesLimits[property.Name] = value;
                }
            }
        }
    }

    private static void FillContainerVolumeMounts(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("VolumeMounts", out var mountsElement) || mountsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var mountElement in mountsElement.EnumerateArray())
        {
            var name = mountElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var mountPath = mountElement.TryGetProperty("value", out var valueProp) ? valueProp.GetString() ?? string.Empty : string.Empty;
            var subPath = mountElement.TryGetProperty("option", out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mountPath))
            {
                continue;
            }

            container.VolumeMounts.Add(new VolumeMountSpec
            {
                Name = name,
                MountPath = mountPath,
                SubPath = subPath
            });
        }
    }

    private static void FillContainerConfigMapEnvFrom(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("ConfigMapEnvFromSource", out var envFromElement) || envFromElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var itemElement in envFromElement.EnumerateArray())
        {
            var name = itemElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;

            if (!string.IsNullOrWhiteSpace(name))
            {
                container.ConfigMapEnvFromSource.Add(name);
            }
        }
    }

    private static void FillContainerProbes(JsonElement element, ContainerSpec container)
    {
        if (element.TryGetProperty("LivenessProbe", out var livenessElement))
        {
            var livenessProbe = ParseProbe(livenessElement);

            if (livenessProbe != null)
            {
                container.LivenessProbe = livenessProbe;
            }
        }

        if (element.TryGetProperty("ReadinessProbe", out var readinessElement))
        {
            var readinessProbe = ParseProbe(readinessElement);

            if (readinessProbe != null)
            {
                container.ReadinessProbe = readinessProbe;
            }
        }

        if (element.TryGetProperty("StartupProbe", out var startupElement))
        {
            var startupProbe = ParseProbe(startupElement);

            if (startupProbe != null)
            {
                container.StartupProbe = startupProbe;
            }
        }
    }

    private static ProbeSpec? ParseProbe(JsonElement probeElement)
    {
        if (probeElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new ProbeSpec
        {
            FailureThreshold = GetOptionalString(probeElement, "failureThreshold"),
            InitialDelaySeconds = GetOptionalString(probeElement, "initialDelaySeconds"),
            PeriodSeconds = GetOptionalString(probeElement, "periodSeconds"),
            SuccessThreshold = GetOptionalString(probeElement, "successThreshold"),
            TimeoutSeconds = GetOptionalString(probeElement, "timeoutSeconds"),
            Type = GetOptionalString(probeElement, "type")
        };

        if (probeElement.TryGetProperty("exec", out var execElement))
        {
            result.Exec = ParseExecAction(execElement);
        }

        if (probeElement.TryGetProperty("httpGet", out var httpGetElement))
        {
            result.HttpGet = ParseHttpGetAction(httpGetElement);
        }

        if (probeElement.TryGetProperty("tcpSocket", out var tcpSocketElement))
        {
            result.TcpSocket = ParseTcpSocketAction(tcpSocketElement);
        }

        if (result.Exec == null && result.HttpGet == null && result.TcpSocket == null
            && string.IsNullOrWhiteSpace(result.FailureThreshold)
            && string.IsNullOrWhiteSpace(result.InitialDelaySeconds)
            && string.IsNullOrWhiteSpace(result.PeriodSeconds)
            && string.IsNullOrWhiteSpace(result.SuccessThreshold)
            && string.IsNullOrWhiteSpace(result.TimeoutSeconds)
            && string.IsNullOrWhiteSpace(result.Type))
        {
            return null;
        }

        return result;
    }

    private static ExecActionSpec? ParseExecAction(JsonElement execElement)
    {
        if (execElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!execElement.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new ExecActionSpec();

        foreach (var item in commandElement.EnumerateArray())
        {
            var value = item.GetString();

            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Command.Add(value);
            }
        }

        if (result.Command.Count == 0)
        {
            return null;
        }

        return result;
    }

    private static HttpGetActionSpec? ParseHttpGetAction(JsonElement httpGetElement)
    {
        if (httpGetElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new HttpGetActionSpec
        {
            Host = GetOptionalString(httpGetElement, "host"),
            Path = GetOptionalString(httpGetElement, "path"),
            Port = GetOptionalString(httpGetElement, "port"),
            Scheme = GetOptionalString(httpGetElement, "scheme")
        };

        if (httpGetElement.TryGetProperty("httpHeaders", out var headersElement) && headersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var headerElement in headersElement.EnumerateArray())
            {
                if (headerElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = headerElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var value = headerElement.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.HttpHeaders.Add(new HttpHeaderSpec
                    {
                        Name = name,
                        Value = value
                    });
                }
            }
        }

        if (string.IsNullOrWhiteSpace(result.Host)
            && string.IsNullOrWhiteSpace(result.Path)
            && string.IsNullOrWhiteSpace(result.Port)
            && string.IsNullOrWhiteSpace(result.Scheme)
            && result.HttpHeaders.Count == 0)
        {
            return null;
        }

        return result;
    }

    private static TcpSocketActionSpec? ParseTcpSocketAction(JsonElement tcpSocketElement)
    {
        if (tcpSocketElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new TcpSocketActionSpec
        {
            Host = GetOptionalString(tcpSocketElement, "host"),
            Port = GetOptionalString(tcpSocketElement, "port")
        };

        if (string.IsNullOrWhiteSpace(result.Host) && string.IsNullOrWhiteSpace(result.Port))
        {
            return null;
        }

        return result;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }



    private static void FillContainerSecurityContext(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("SecurityContext", out var contextElement) || contextElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var securityContext = new SecurityContextSpec
        {
            AllowPrivilegeEscalation = GetOptionalString(contextElement, "allowPrivilegeEscalation"),
            Privileged = GetOptionalString(contextElement, "privileged"),
            ReadOnlyRootFilesystem = GetOptionalString(contextElement, "readOnlyRootFilesystem"),
            RunAsGroup = GetOptionalString(contextElement, "runAsGroup"),
            RunAsNonRoot = GetOptionalString(contextElement, "runAsNonRoot"),
            RunAsUser = GetOptionalString(contextElement, "runAsUser")
        };

        if (contextElement.TryGetProperty("capabilities", out var capabilitiesElement) && capabilitiesElement.ValueKind == JsonValueKind.Object)
        {
            var capabilities = new CapabilitiesSpec();

            if (capabilitiesElement.TryGetProperty("add", out var addElement) && addElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in addElement.EnumerateArray())
                {
                    var value = item.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        capabilities.Add.Add(value);
                    }
                }
            }

            if (capabilitiesElement.TryGetProperty("drop", out var dropElement) && dropElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dropElement.EnumerateArray())
                {
                    var value = item.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        capabilities.Drop.Add(value);
                    }
                }
            }

            if (capabilities.Add.Count > 0 || capabilities.Drop.Count > 0)
            {
                securityContext.Capabilities = capabilities;
            }
        }

        if (contextElement.TryGetProperty("seLinuxOptions", out var seLinuxOptionsElement) && seLinuxOptionsElement.ValueKind == JsonValueKind.Object)
        {
            var seLinuxOptions = new SeLinuxOptionsSpec
            {
                Level = GetOptionalString(seLinuxOptionsElement, "level"),
                Role = GetOptionalString(seLinuxOptionsElement, "role"),
                Type = GetOptionalString(seLinuxOptionsElement, "type"),
                User = GetOptionalString(seLinuxOptionsElement, "user")
            };

            if (!string.IsNullOrWhiteSpace(seLinuxOptions.Level)
                || !string.IsNullOrWhiteSpace(seLinuxOptions.Role)
                || !string.IsNullOrWhiteSpace(seLinuxOptions.Type)
                || !string.IsNullOrWhiteSpace(seLinuxOptions.User))
            {
                securityContext.SeLinuxOptions = seLinuxOptions;
            }
        }

        if (string.IsNullOrWhiteSpace(securityContext.AllowPrivilegeEscalation)
            && string.IsNullOrWhiteSpace(securityContext.Privileged)
            && string.IsNullOrWhiteSpace(securityContext.ReadOnlyRootFilesystem)
            && string.IsNullOrWhiteSpace(securityContext.RunAsGroup)
            && string.IsNullOrWhiteSpace(securityContext.RunAsNonRoot)
            && string.IsNullOrWhiteSpace(securityContext.RunAsUser)
            && securityContext.Capabilities == null
            && securityContext.SeLinuxOptions == null)
        {
            return;
        }

        container.SecurityContext = securityContext;
    }

    private static void FillContainerLifecycle(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("Lifecycle", out var lifecycleElement) || lifecycleElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var lifecycle = new LifecycleSpec
        {
            PreStop = ParseLifecycleHandler(lifecycleElement, "PreStop"),
            PostStart = ParseLifecycleHandler(lifecycleElement, "PostStart")
        };

        if (lifecycle.PreStop == null && lifecycle.PostStart == null)
        {
            return;
        }

        container.Lifecycle = lifecycle;
    }

    private static LifecycleHandlerSpec? ParseLifecycleHandler(JsonElement lifecycleElement, string propertyName)
    {
        if (!lifecycleElement.TryGetProperty(propertyName, out var handlerElement) || handlerElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var handler = new LifecycleHandlerSpec();

        if (handlerElement.TryGetProperty("exec", out var execElement))
        {
            handler.Exec = ParseExecAction(execElement);
        }

        if (handlerElement.TryGetProperty("httpGet", out var httpGetElement))
        {
            handler.HttpGet = ParseHttpGetAction(httpGetElement);
        }

        if (handlerElement.TryGetProperty("tcpSocket", out var tcpSocketElement))
        {
            handler.TcpSocket = ParseTcpSocketAction(tcpSocketElement);
        }

        if (handler.Exec == null && handler.HttpGet == null && handler.TcpSocket == null)
        {
            return null;
        }

        return handler;
    }


    private static void AppendKeyValueIfNotNullOrWhiteSpace(StringBuilder sb, string indent, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append(indent);
        sb.Append(key);
        sb.Append(": ");
        sb.AppendLine(value);
    }

    private static void AppendProbeYaml(StringBuilder sb, string indent, string name, ProbeSpec probe)
    {
        sb.Append(indent);
        sb.AppendLine($"{name}:");

        var innerIndent = indent + "  ";

        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "failureThreshold", probe.FailureThreshold);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "initialDelaySeconds", probe.InitialDelaySeconds);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "periodSeconds", probe.PeriodSeconds);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "successThreshold", probe.SuccessThreshold);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "timeoutSeconds", probe.TimeoutSeconds);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "type", probe.Type);

        if (probe.Exec != null && probe.Exec.Command.Count > 0)
        {
            sb.Append(innerIndent);
            sb.AppendLine("exec:");
            sb.Append(innerIndent);
            sb.AppendLine("  command:");

            foreach (var command in probe.Exec.Command)
            {
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }



                sb.Append(innerIndent);
                sb.AppendLine($"  - {command}");
            }
        }

        if (probe.HttpGet != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine("httpGet:");

            var httpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "host", probe.HttpGet.Host);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "path", probe.HttpGet.Path);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "port", probe.HttpGet.Port);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "scheme", probe.HttpGet.Scheme);

            if (probe.HttpGet.HttpHeaders.Count > 0)
            {
                sb.Append(httpIndent);
                sb.AppendLine("httpHeaders:");

                foreach (var header in probe.HttpGet.HttpHeaders)
                {
                    if (string.IsNullOrWhiteSpace(header.Name))
                    {
                        continue;
                    }

                    sb.Append(httpIndent);
                    sb.AppendLine($"  - name: {header.Name}");

                    if (!string.IsNullOrWhiteSpace(header.Value))
                    {
                        sb.Append(httpIndent);
                        sb.AppendLine($"    value: {header.Value}");
                    }
                }
            }
        }

        if (probe.TcpSocket != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine("tcpSocket:");

            var tcpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, tcpIndent, "host", probe.TcpSocket.Host);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, tcpIndent, "port", probe.TcpSocket.Port);
        }
    }

    private static void AppendLifecycleHandlerYaml(
        StringBuilder sb,
        string indent,
        string name,
        LifecycleHandlerSpec handler)
    {
        sb.Append(indent);
        sb.AppendLine($"{name}:");

        var innerIndent = indent + "  ";

        if (handler.Exec != null && handler.Exec.Command.Count > 0)
        {
            sb.Append(innerIndent);
            sb.AppendLine("exec:");
            sb.Append(innerIndent);
            sb.AppendLine("  command:");

            foreach (var command in handler.Exec.Command)
            {
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                sb.Append(innerIndent);
                sb.AppendLine($"  - {command}");
            }
        }

        if (handler.HttpGet != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine("httpGet:");

            var httpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "host", handler.HttpGet.Host);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "path", handler.HttpGet.Path);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "port", handler.HttpGet.Port);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, "scheme", handler.HttpGet.Scheme);

            if (handler.HttpGet.HttpHeaders.Count > 0)
            {
                sb.Append(httpIndent);
                sb.AppendLine("httpHeaders:");

                foreach (var header in handler.HttpGet.HttpHeaders)
                {
                    if (string.IsNullOrWhiteSpace(header.Name))
                    {
                        continue;
                    }

                    sb.Append(httpIndent);
                    sb.AppendLine($"  - name: {header.Name}");

                    if (!string.IsNullOrWhiteSpace(header.Value))
                    {
                        sb.Append(httpIndent);
                        sb.AppendLine($"    value: {header.Value}");
                    }
                }
            }
        }

        if (handler.TcpSocket != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine("tcpSocket:");

            var tcpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, tcpIndent, "host", handler.TcpSocket.Host);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, tcpIndent, "port", handler.TcpSocket.Port);
        }
    }

    private static void AppendJsonFromProperty(
        StringBuilder sb,
        string indent,
        string key,
        Dictionary<string, string> properties,
        string propertyName)
    {
        if (!properties.TryGetValue(propertyName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        raw = raw.Trim();

        if (raw == "[]" || raw == "{}")
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);

            sb.Append(indent);
            sb.AppendLine($"{key}:");

            AppendJsonElementYaml(sb, indent + "  ", doc.RootElement);
        }
        catch
        {
        }
    }

    private static void AppendJsonElementYaml(StringBuilder sb, string indent, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    var value = property.Value;

                    if (value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array)
                    {
                        sb.Append(indent);
                        sb.AppendLine($"{name}:");

                        AppendJsonElementYaml(sb, indent + "  ", value);
                    }
                    else
                    {
                        sb.Append(indent);
                        sb.Append($"{name}: ");
                        AppendJsonScalarValue(sb, value);
                        sb.AppendLine();
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        sb.Append(indent);
                        sb.AppendLine("-");

                        AppendJsonElementYaml(sb, indent + "  ", item);
                    }
                    else if (item.ValueKind == JsonValueKind.Array)
                    {
                        sb.Append(indent);
                        sb.AppendLine("-");

                        AppendJsonElementYaml(sb, indent + "  ", item);
                    }
                    else
                    {
                        sb.Append(indent);
                        sb.Append("- ");
                        AppendJsonScalarValue(sb, item);
                        sb.AppendLine();
                    }
                }

                break;

            default:
                sb.Append(indent);
                AppendJsonScalarValue(sb, element);
                sb.AppendLine();
                break;
        }
    }

    private static void AppendJsonScalarValue(StringBuilder sb, JsonElement element)
    {
        sb.Append(element.GetRawText());
    }


    private sealed class ServicePortSpec
    {
        public string Name { get; set; } = string.Empty;

        public int Port { get; set; }

        public int? TargetPort { get; set; }

        public int? NodePort { get; set; }

        public string Protocol { get; set; } = string.Empty;
    }

    private sealed class ContainerSpec
    {
        public string Name { get; set; } = string.Empty;

        public string Image { get; set; } = string.Empty;

        public List<ContainerPortSpec> Ports { get; } = new();

        public Dictionary<string, string> ResourcesRequests { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> ResourcesLimits { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<VolumeMountSpec> VolumeMounts { get; } = new();

        public List<string> ConfigMapEnvFromSource { get; } = new();

        public ProbeSpec? LivenessProbe { get; set; }

        public ProbeSpec? ReadinessProbe { get; set; }

        public ProbeSpec? StartupProbe { get; set; }

        public SecurityContextSpec? SecurityContext { get; set; }

        public LifecycleSpec? Lifecycle { get; set; }
    }

    private sealed class ContainerPortSpec
    {
        public string Name { get; set; } = string.Empty;

        public int Port { get; set; }

        public string Protocol { get; set; } = string.Empty;
    }

    private sealed class VolumeSpec
    {
        public string Name { get; set; } = string.Empty;

        public string? ConfigMapName { get; set; }
    }

    private sealed class ProbeSpec
    {
        public string? FailureThreshold { get; set; }

        public string? InitialDelaySeconds { get; set; }

        public string? PeriodSeconds { get; set; }

        public string? SuccessThreshold { get; set; }

        public string? TimeoutSeconds { get; set; }

        public string? Type { get; set; }

        public ExecActionSpec? Exec { get; set; }

        public HttpGetActionSpec? HttpGet { get; set; }

        public TcpSocketActionSpec? TcpSocket { get; set; }
    }

    private sealed class ExecActionSpec
    {
        public List<string> Command { get; } = new();
    }

    private sealed class HttpGetActionSpec
    {
        public string? Host { get; set; }

        public string? Path { get; set; }

        public string? Port { get; set; }

        public string? Scheme { get; set; }

        public List<HttpHeaderSpec> HttpHeaders { get; } = new();
    }

    private sealed class HttpHeaderSpec
    {
        public string Name { get; set; } = string.Empty;

        public string? Value { get; set; }
    }

    private sealed class TcpSocketActionSpec
    {
        public string? Host { get; set; }

        public string? Port { get; set; }
    }

    private sealed class SecurityContextSpec
    {
        public string? AllowPrivilegeEscalation { get; set; }

        public string? Privileged { get; set; }

        public string? ReadOnlyRootFilesystem { get; set; }

        public string? RunAsGroup { get; set; }

        public string? RunAsNonRoot { get; set; }

        public string? RunAsUser { get; set; }

        public CapabilitiesSpec? Capabilities { get; set; }

        public SeLinuxOptionsSpec? SeLinuxOptions { get; set; }
    }

    private sealed class CapabilitiesSpec
    {
        public List<string> Add { get; } = new();

        public List<string> Drop { get; } = new();
    }

    private sealed class SeLinuxOptionsSpec
    {
        public string? Level { get; set; }

        public string? Role { get; set; }

        public string? Type { get; set; }

        public string? User { get; set; }
    }

    private sealed class LifecycleSpec
    {
        public LifecycleHandlerSpec? PreStop { get; set; }

        public LifecycleHandlerSpec? PostStart { get; set; }
    }

    private sealed class LifecycleHandlerSpec
    {
        public ExecActionSpec? Exec { get; set; }

        public HttpGetActionSpec? HttpGet { get; set; }

        public TcpSocketActionSpec? TcpSocket { get; set; }
    }

    private sealed class VolumeMountSpec
    {
        public string Name { get; set; } = string.Empty;

        public string MountPath { get; set; } = string.Empty;

        public string? SubPath { get; set; }
    }
}
