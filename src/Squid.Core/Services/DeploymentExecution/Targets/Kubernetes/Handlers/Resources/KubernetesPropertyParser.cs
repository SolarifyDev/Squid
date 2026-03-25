using System.Globalization;
using System.Text;
using System.Text.Json;
using Serilog;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesPropertyParser
{
    internal static Dictionary<string, string> BuildPropertyDictionary(DeploymentActionDto action)
    {
        if (action.Properties == null || action.Properties.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, string>(action.Properties.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var prop in action.Properties)
            dict[prop.PropertyName] = prop.PropertyValue;

        return dict;
    }

    internal static string GetProperty(Dictionary<string, string> properties, string name)
    {
        if (properties.TryGetValue(name, out var value))
            return value;

        return string.Empty;
    }

    internal static string GetNamespace(Dictionary<string, string> properties)
    {
        var ns = GetProperty(properties, KubernetesProperties.Namespace);

        if (string.IsNullOrWhiteSpace(ns))
            ns = GetProperty(properties, KubernetesProperties.LegacyNamespace);

        if (string.IsNullOrWhiteSpace(ns))
            ns = KubernetesDefaultValues.Namespace;

        return ns;
    }

    internal static Dictionary<string, string> ParseStringDictionaryProperty(Dictionary<string, string> properties, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!properties.TryGetValue(propertyName, out var raw) || string.IsNullOrWhiteSpace(raw))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                        continue;

                    var key = element.TryGetProperty(KubernetesKeyValuePayloadProperties.PascalKey, out var keyProp) ? keyProp.GetString()
                        : element.TryGetProperty(KubernetesKeyValuePayloadProperties.LowerKey, out var lowerKeyProp) ? lowerKeyProp.GetString()
                        : null;

                    var value = element.TryGetProperty(KubernetesKeyValuePayloadProperties.PascalValue, out var valueProp) ? valueProp.GetString()
                        : element.TryGetProperty(KubernetesKeyValuePayloadProperties.LowerValue, out var lowerValueProp) ? lowerValueProp.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(key) || value == null)
                        continue;

                    result[key] = value;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    var key = property.Name;

                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    result[key] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse {PropertyName} as string dictionary", propertyName);
        }

        return result;
    }

    internal static List<ServicePortSpec> ParseServicePorts(Dictionary<string, string> properties)
    {
        var result = new List<ServicePortSpec>();

        var portsJson = GetProperty(properties, KubernetesProperties.ServicePorts);

        if (string.IsNullOrWhiteSpace(portsJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(portsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty(KubernetesServicePortPayloadProperties.Name, out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var portText = element.TryGetProperty(KubernetesServicePortPayloadProperties.Port, out var portProp) ? GetStringOrNumber(portProp) : null;
                var targetPortText = element.TryGetProperty(KubernetesServicePortPayloadProperties.TargetPort, out var targetPortProp) ? GetStringOrNumber(targetPortProp) : null;
                var nodePortText = element.TryGetProperty(KubernetesServicePortPayloadProperties.NodePort, out var nodePortProp) ? GetStringOrNumber(nodePortProp) : null;
                var protocol = element.TryGetProperty(KubernetesServicePortPayloadProperties.Protocol, out var protocolProp) ? protocolProp.GetString() ?? string.Empty : string.Empty;

                if (!int.TryParse(portText, out var port))
                    continue;

                int? nodePort = null;

                if (int.TryParse(nodePortText, out var parsedNodePort))
                    nodePort = parsedNodePort;

                result.Add(new ServicePortSpec
                {
                    Name = string.IsNullOrWhiteSpace(name) ? KubernetesDefaultValues.PortName : name,
                    Port = port,
                    TargetPort = string.IsNullOrWhiteSpace(targetPortText) ? null : targetPortText,
                    NodePort = nodePort,
                    Protocol = string.IsNullOrWhiteSpace(protocol) ? KubernetesDefaultValues.ProtocolTcp : protocol
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse service ports JSON");
        }

        return result;
    }

    internal static List<ContainerSpec> ParseContainers(Dictionary<string, string> properties)
    {
        var result = new List<ContainerSpec>();

        var containersJson = GetProperty(properties, KubernetesProperties.Containers);

        if (string.IsNullOrWhiteSpace(containersJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(containersJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty(KubernetesContainerPayloadProperties.Name, out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var image = GetFirstImagePropertyFromContainer(element)
                    ?? throw new InvalidOperationException($"ContainerImage is required for container '{name}' but was not resolved. Ensure the package feed and version are configured correctly.");

                var container = new ContainerSpec
                {
                    Name = string.IsNullOrWhiteSpace(name) ? KubernetesDefaultValues.ContainerName : name,
                    Image = image
                };

                FillContainerPorts(element, container);
                FillContainerResources(element, container);
                FillContainerVolumeMounts(element, container);
                FillContainerConfigMapEnvFrom(element, container);
                FillContainerProbes(element, container);
                FillContainerSecurityContext(element, container);
                FillContainerLifecycle(element, container);
                FillContainerEnvironmentVariables(element, container);
                FillContainerConfigMapEnvVariables(element, container);
                FillContainerSecretEnvVariables(element, container);
                FillContainerFieldRefEnvVariables(element, container);
                FillContainerSecretEnvFromSource(element, container);
                FillContainerSimpleProperties(element, container);

                container.IsInitContainer = element.TryGetProperty(KubernetesContainerPayloadProperties.IsInitContainer, out var initProp)
                    && string.Equals(initProp.GetString(), KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase);

                result.Add(container);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse containers JSON");
        }

        return result;
    }

    internal static List<VolumeSpec> ParseVolumes(Dictionary<string, string> properties)
    {
        var result = new List<VolumeSpec>();

        var combinedVolumesJson = GetProperty(properties, KubernetesProperties.CombinedVolumes);

        if (string.IsNullOrWhiteSpace(combinedVolumesJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(combinedVolumesJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty(KubernetesVolumePayloadProperties.Name, out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var type = element.TryGetProperty(KubernetesVolumePayloadProperties.Type, out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
                var referenceName = element.TryGetProperty(KubernetesVolumePayloadProperties.ReferenceName, out var referenceProp) ? referenceProp.GetString() ?? string.Empty : string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var volume = new VolumeSpec { Name = name };

                if (string.Equals(type, KubernetesVolumeTypeValues.ConfigMap, StringComparison.OrdinalIgnoreCase))
                    volume.ConfigMapName = referenceName;
                else if (string.Equals(type, KubernetesVolumeTypeValues.Secret, StringComparison.OrdinalIgnoreCase))
                    volume.SecretName = referenceName;
                else if (string.Equals(type, KubernetesVolumeTypeValues.EmptyDir, StringComparison.OrdinalIgnoreCase))
                    volume.EmptyDir = true;
                else if (string.Equals(type, KubernetesVolumeTypeValues.PersistentVolumeClaim, StringComparison.OrdinalIgnoreCase))
                    volume.PvcClaimName = referenceName;
                else if (string.Equals(type, KubernetesVolumeTypeValues.HostPath, StringComparison.OrdinalIgnoreCase))
                    volume.HostPath = referenceName;

                result.Add(volume);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse volumes JSON");
        }

        return result;
    }

    private static string GetFirstImagePropertyFromContainer(JsonElement containerElement)
    {
        if (containerElement.TryGetProperty(KubernetesContainerPayloadProperties.Image, out var imageProp))
        {
            var image = imageProp.GetString();

            if (!string.IsNullOrWhiteSpace(image))
                return image;
        }

        if (containerElement.TryGetProperty(KubernetesContainerPayloadProperties.PackageId, out var packageIdProp))
        {
            var packageId = packageIdProp.GetString();

            if (!string.IsNullOrWhiteSpace(packageId))
                return packageId;
        }

        return null;
    }

    private static void FillContainerPorts(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.Ports, out var portsElement) || portsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var portElement in portsElement.EnumerateArray())
        {
            var name = portElement.TryGetProperty(KubernetesContainerPortPayloadProperties.Name, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var portText = portElement.TryGetProperty(KubernetesContainerPortPayloadProperties.ContainerPort, out var valueProp) ? valueProp.GetString() : null;
            var protocol = portElement.TryGetProperty(KubernetesContainerPortPayloadProperties.Protocol, out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;

            if (!int.TryParse(portText, out var port))
                continue;

            container.Ports.Add(new ContainerPortSpec
            {
                Name = string.IsNullOrWhiteSpace(name) ? KubernetesDefaultValues.PortName : name,
                Port = port,
                Protocol = string.IsNullOrWhiteSpace(protocol) ? KubernetesDefaultValues.ProtocolTcp : protocol
            });
        }
    }

    private static void FillContainerResources(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.Resources, out var resourcesElement) || resourcesElement.ValueKind != JsonValueKind.Object)
            return;

        if (resourcesElement.TryGetProperty(KubernetesContainerResourcePayloadProperties.Requests, out var requestsElement) && requestsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in requestsElement.EnumerateObject())
            {
                var value = property.Value.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    container.ResourcesRequests[property.Name] = value;
            }
        }

        if (resourcesElement.TryGetProperty(KubernetesContainerResourcePayloadProperties.Limits, out var limitsElement) && limitsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in limitsElement.EnumerateObject())
            {
                var value = property.Value.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    container.ResourcesLimits[property.Name] = value;
            }
        }
    }

    private static void FillContainerVolumeMounts(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.VolumeMounts, out var mountsElement) || mountsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var mountElement in mountsElement.EnumerateArray())
        {
            var name = mountElement.TryGetProperty(KubernetesContainerVolumeMountPayloadProperties.VolumeName, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var mountPath = mountElement.TryGetProperty(KubernetesContainerVolumeMountPayloadProperties.MountPath, out var valueProp) ? valueProp.GetString() ?? string.Empty : string.Empty;
            var subPath = mountElement.TryGetProperty(KubernetesContainerVolumeMountPayloadProperties.SubPath, out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mountPath))
                continue;

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
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.ConfigMapEnvFromSource, out var envFromElement) || envFromElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemElement in envFromElement.EnumerateArray())
        {
            var name = itemElement.TryGetProperty(KubernetesContainerEnvFromPayloadProperties.Name, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var spec = new EnvFromSpec { Name = name };
            spec.Prefix = GetOptionalString(itemElement, KubernetesContainerEnvFromPayloadProperties.Prefix);
            spec.Optional = ParseOptionalBool(itemElement, KubernetesContainerEnvFromPayloadProperties.Optional);
            container.ConfigMapEnvFromSource.Add(spec);
        }
    }

    private static void FillContainerEnvironmentVariables(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.EnvironmentVariables, out var envElement) || envElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemElement in envElement.EnumerateArray())
        {
            var key = itemElement.TryGetProperty(KubernetesContainerEnvVarPayloadProperties.Key, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var value = itemElement.TryGetProperty(KubernetesContainerEnvVarPayloadProperties.Value, out var valueProp) ? valueProp.GetString() ?? string.Empty : string.Empty;

            if (!string.IsNullOrWhiteSpace(key))
                container.EnvironmentVariables.Add(new EnvVarSpec { Name = key, Value = value });
        }
    }

    private static void FillContainerConfigMapEnvVariables(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.ConfigMapEnvironmentVariables, out var envElement) || envElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemElement in envElement.EnumerateArray())
        {
            var envVarName = itemElement.TryGetProperty(KubernetesContainerEnvVarSourcePayloadProperties.Key, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var sourceName = itemElement.TryGetProperty(KubernetesContainerEnvVarSourcePayloadProperties.Value, out var valueProp) ? valueProp.GetString() ?? string.Empty : string.Empty;
            var sourceKey = itemElement.TryGetProperty(KubernetesContainerEnvVarSourcePayloadProperties.Option, out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;
            var optional = ParseOptionalBool(itemElement, KubernetesContainerEnvVarSourcePayloadProperties.Optional);

            if (!string.IsNullOrWhiteSpace(envVarName))
                container.ConfigMapEnvVariables.Add(new EnvVarSourceSpec { EnvVarName = envVarName, SourceName = sourceName, SourceKey = sourceKey, Optional = optional });
        }
    }

    private static void FillContainerSecretEnvVariables(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.SecretEnvironmentVariables, out var envElement) || envElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemElement in envElement.EnumerateArray())
        {
            var envVarName = itemElement.TryGetProperty(KubernetesContainerEnvVarSourcePayloadProperties.Key, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var sourceName = itemElement.TryGetProperty(KubernetesContainerEnvVarSourcePayloadProperties.Value, out var valueProp) ? valueProp.GetString() ?? string.Empty : string.Empty;
            var sourceKey = itemElement.TryGetProperty(KubernetesContainerEnvVarSourcePayloadProperties.Option, out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;
            var optional = ParseOptionalBool(itemElement, KubernetesContainerEnvVarSourcePayloadProperties.Optional);

            if (!string.IsNullOrWhiteSpace(envVarName))
                container.SecretEnvVariables.Add(new EnvVarSourceSpec { EnvVarName = envVarName, SourceName = sourceName, SourceKey = sourceKey, Optional = optional });
        }
    }

    private static void FillContainerFieldRefEnvVariables(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.FieldRefEnvironmentVariables, out var envElement) || envElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemElement in envElement.EnumerateArray())
        {
            var envVarName = itemElement.TryGetProperty(KubernetesContainerFieldRefPayloadProperties.Key, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var fieldPath = itemElement.TryGetProperty(KubernetesContainerFieldRefPayloadProperties.Value, out var valueProp) ? valueProp.GetString() ?? string.Empty : string.Empty;

            if (!string.IsNullOrWhiteSpace(envVarName))
                container.FieldRefEnvVariables.Add(new FieldRefEnvVarSpec { EnvVarName = envVarName, FieldPath = fieldPath });
        }
    }

    private static void FillContainerSecretEnvFromSource(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.SecretEnvFromSource, out var envFromElement) || envFromElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemElement in envFromElement.EnumerateArray())
        {
            var name = itemElement.TryGetProperty(KubernetesContainerSecretEnvFromPayloadProperties.Name, out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var spec = new EnvFromSpec { Name = name };
            spec.Prefix = GetOptionalString(itemElement, KubernetesContainerSecretEnvFromPayloadProperties.Prefix);
            spec.Optional = ParseOptionalBool(itemElement, KubernetesContainerSecretEnvFromPayloadProperties.Optional);
            container.SecretEnvFromSource.Add(spec);
        }
    }

    private static void FillContainerSimpleProperties(JsonElement element, ContainerSpec container)
    {
        container.ImagePullPolicy = GetOptionalString(element, KubernetesContainerPayloadProperties.ImagePullPolicy);
        container.TerminationMessagePath = GetOptionalString(element, KubernetesContainerPayloadProperties.TerminationMessagePath);
        container.TerminationMessagePolicy = GetOptionalString(element, KubernetesContainerPayloadProperties.TerminationMessagePolicy);

        ParseStringList(element, KubernetesContainerPayloadProperties.Command, container.Command);
        ParseStringList(element, KubernetesContainerPayloadProperties.Args, container.Args);
    }

    private static void ParseStringList(JsonElement element, string propertyName, List<string> target)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return;

        if (prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                var value = item.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    target.Add(value);
            }
        }
        else if (prop.ValueKind == JsonValueKind.String)
        {
            foreach (var line in (prop.GetString() ?? string.Empty).Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');

                if (!string.IsNullOrWhiteSpace(trimmed))
                    target.Add(trimmed);
            }
        }
    }

    private static void FillContainerProbes(JsonElement element, ContainerSpec container)
    {
        if (element.TryGetProperty(KubernetesContainerPayloadProperties.LivenessProbe, out var livenessElement))
        {
            var probe = ParseProbe(livenessElement);

            if (probe != null)
                container.LivenessProbe = probe;
        }

        if (element.TryGetProperty(KubernetesContainerPayloadProperties.ReadinessProbe, out var readinessElement))
        {
            var probe = ParseProbe(readinessElement);

            if (probe != null)
                container.ReadinessProbe = probe;
        }

        if (element.TryGetProperty(KubernetesContainerPayloadProperties.StartupProbe, out var startupElement))
        {
            var probe = ParseProbe(startupElement);

            if (probe != null)
                container.StartupProbe = probe;
        }
    }

    private static ProbeSpec? ParseProbe(JsonElement probeElement)
    {
        if (probeElement.ValueKind != JsonValueKind.Object)
            return null;

        var result = new ProbeSpec
        {
            FailureThreshold = GetOptionalString(probeElement, KubernetesContainerProbePayloadProperties.FailureThreshold),
            InitialDelaySeconds = GetOptionalString(probeElement, KubernetesContainerProbePayloadProperties.InitialDelaySeconds),
            PeriodSeconds = GetOptionalString(probeElement, KubernetesContainerProbePayloadProperties.PeriodSeconds),
            SuccessThreshold = GetOptionalString(probeElement, KubernetesContainerProbePayloadProperties.SuccessThreshold),
            TimeoutSeconds = GetOptionalString(probeElement, KubernetesContainerProbePayloadProperties.TimeoutSeconds)
        };

        if (probeElement.TryGetProperty(KubernetesContainerProbePayloadProperties.Exec, out var execElement))
            result.Exec = ParseExecAction(execElement);

        if (probeElement.TryGetProperty(KubernetesContainerProbePayloadProperties.HttpGet, out var httpGetElement))
            result.HttpGet = ParseHttpGetAction(httpGetElement);

        if (probeElement.TryGetProperty(KubernetesContainerProbePayloadProperties.TcpSocket, out var tcpSocketElement))
            result.TcpSocket = ParseTcpSocketAction(tcpSocketElement);

        if (result.Exec == null && result.HttpGet == null && result.TcpSocket == null
            && string.IsNullOrWhiteSpace(result.FailureThreshold)
            && string.IsNullOrWhiteSpace(result.InitialDelaySeconds)
            && string.IsNullOrWhiteSpace(result.PeriodSeconds)
            && string.IsNullOrWhiteSpace(result.SuccessThreshold)
            && string.IsNullOrWhiteSpace(result.TimeoutSeconds))
        {
            return null;
        }

        return result;
    }

    private static ExecActionSpec? ParseExecAction(JsonElement execElement)
    {
        if (execElement.ValueKind != JsonValueKind.Object)
            return null;

        if (!execElement.TryGetProperty(KubernetesProbeActionPayloadProperties.Command, out var commandElement))
            return null;

        var result = new ExecActionSpec();

        if (commandElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in commandElement.EnumerateArray())
            {
                var value = item.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    result.Command.Add(value);
            }
        }
        else if (commandElement.ValueKind == JsonValueKind.String)
        {
            foreach (var line in (commandElement.GetString() ?? string.Empty).Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');

                if (!string.IsNullOrWhiteSpace(trimmed))
                    result.Command.Add(trimmed);
            }
        }

        if (result.Command.Count == 0)
            return null;

        return result;
    }

    private static HttpGetActionSpec? ParseHttpGetAction(JsonElement httpGetElement)
    {
        if (httpGetElement.ValueKind != JsonValueKind.Object)
            return null;

        var result = new HttpGetActionSpec
        {
            Host = GetOptionalString(httpGetElement, KubernetesProbeActionPayloadProperties.Host),
            Path = GetOptionalString(httpGetElement, KubernetesProbeActionPayloadProperties.Path),
            Port = GetOptionalString(httpGetElement, KubernetesProbeActionPayloadProperties.Port),
            Scheme = GetOptionalString(httpGetElement, KubernetesProbeActionPayloadProperties.Scheme)
        };

        if (httpGetElement.TryGetProperty(KubernetesProbeActionPayloadProperties.HttpHeaders, out var headersElement) && headersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var headerElement in headersElement.EnumerateArray())
            {
                if (headerElement.ValueKind != JsonValueKind.Object)
                    continue;

                var name = headerElement.TryGetProperty(KubernetesProbeActionPayloadProperties.Name, out var nameProp) ? nameProp.GetString() : null;
                var value = headerElement.TryGetProperty(KubernetesProbeActionPayloadProperties.Value, out var valueProp) ? valueProp.GetString() : null;

                if (!string.IsNullOrWhiteSpace(name))
                    result.HttpHeaders.Add(new HttpHeaderSpec { Name = name, Value = value });
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
            return null;

        var result = new TcpSocketActionSpec
        {
            Host = GetOptionalString(tcpSocketElement, KubernetesProbeActionPayloadProperties.Host),
            Port = GetOptionalString(tcpSocketElement, KubernetesProbeActionPayloadProperties.Port)
        };

        if (string.IsNullOrWhiteSpace(result.Host) && string.IsNullOrWhiteSpace(result.Port))
            return null;

        return result;
    }

    private static string? GetStringOrNumber(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetRawText() : element.GetString();

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }

    private static bool? ParseOptionalBool(JsonElement element, string propertyName)
    {
        var raw = GetOptionalString(element, propertyName);

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return string.Equals(raw, KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase);
    }

    private static void FillContainerSecurityContext(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.SecurityContext, out var contextElement) || contextElement.ValueKind != JsonValueKind.Object)
            return;

        var securityContext = new SecurityContextSpec
        {
            AllowPrivilegeEscalation = GetOptionalString(contextElement, KubernetesContainerSecurityContextPayloadProperties.AllowPrivilegeEscalation),
            Privileged = GetOptionalString(contextElement, KubernetesContainerSecurityContextPayloadProperties.Privileged),
            ReadOnlyRootFilesystem = GetOptionalString(contextElement, KubernetesContainerSecurityContextPayloadProperties.ReadOnlyRootFilesystem),
            RunAsGroup = GetOptionalString(contextElement, KubernetesContainerSecurityContextPayloadProperties.RunAsGroup),
            RunAsNonRoot = GetOptionalString(contextElement, KubernetesContainerSecurityContextPayloadProperties.RunAsNonRoot),
            RunAsUser = GetOptionalString(contextElement, KubernetesContainerSecurityContextPayloadProperties.RunAsUser)
        };

        if (contextElement.TryGetProperty(KubernetesSecurityContextPayloadProperties.Capabilities, out var capabilitiesElement) && capabilitiesElement.ValueKind == JsonValueKind.Object)
        {
            var capabilities = new CapabilitiesSpec();

            if (capabilitiesElement.TryGetProperty(KubernetesSecurityContextPayloadProperties.Add, out var addElement) && addElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in addElement.EnumerateArray())
                {
                    var value = item.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                        capabilities.Add.Add(value);
                }
            }

            if (capabilitiesElement.TryGetProperty(KubernetesSecurityContextPayloadProperties.Drop, out var dropElement) && dropElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dropElement.EnumerateArray())
                {
                    var value = item.GetString();

                    if (!string.IsNullOrWhiteSpace(value))
                        capabilities.Drop.Add(value);
                }
            }

            if (capabilities.Add.Count > 0 || capabilities.Drop.Count > 0)
                securityContext.Capabilities = capabilities;
        }

        if (contextElement.TryGetProperty(KubernetesSecurityContextPayloadProperties.SeLinuxOptions, out var seLinuxOptionsElement) && seLinuxOptionsElement.ValueKind == JsonValueKind.Object)
        {
            var seLinuxOptions = new SeLinuxOptionsSpec
            {
                Level = GetOptionalString(seLinuxOptionsElement, KubernetesSecurityContextPayloadProperties.Level),
                Role = GetOptionalString(seLinuxOptionsElement, KubernetesSecurityContextPayloadProperties.Role),
                Type = GetOptionalString(seLinuxOptionsElement, KubernetesSecurityContextPayloadProperties.Type),
                User = GetOptionalString(seLinuxOptionsElement, KubernetesSecurityContextPayloadProperties.User)
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
        if (!element.TryGetProperty(KubernetesContainerPayloadProperties.Lifecycle, out var lifecycleElement) || lifecycleElement.ValueKind != JsonValueKind.Object)
            return;

        var lifecycle = new LifecycleSpec
        {
            PreStop = ParseLifecycleHandler(lifecycleElement, KubernetesContainerLifecyclePayloadProperties.PreStop),
            PostStart = ParseLifecycleHandler(lifecycleElement, KubernetesContainerLifecyclePayloadProperties.PostStart)
        };

        if (lifecycle.PreStop == null && lifecycle.PostStart == null)
            return;

        container.Lifecycle = lifecycle;
    }

    private static LifecycleHandlerSpec? ParseLifecycleHandler(JsonElement lifecycleElement, string propertyName)
    {
        var handlerElement = default(JsonElement);
        var found = false;

        foreach (var prop in lifecycleElement.EnumerateObject())
        {
            if (!string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            handlerElement = prop.Value;
            found = true;
            break;
        }

        if (!found || handlerElement.ValueKind != JsonValueKind.Object)
            return null;

        var handler = new LifecycleHandlerSpec();
        var type = GetOptionalString(handlerElement, KubernetesContainerLifecyclePayloadProperties.Type) ?? string.Empty;

        if (string.Equals(type, KubernetesContainerProbePayloadProperties.Exec, StringComparison.OrdinalIgnoreCase))
            handler.Exec = ParseExecAction(handlerElement);
        else if (string.Equals(type, KubernetesContainerProbePayloadProperties.HttpGet, StringComparison.OrdinalIgnoreCase))
            handler.HttpGet = ParseHttpGetAction(handlerElement);
        else if (string.Equals(type, KubernetesContainerProbePayloadProperties.TcpSocket, StringComparison.OrdinalIgnoreCase))
            handler.TcpSocket = ParseTcpSocketAction(handlerElement);
        else
        {
            if (handlerElement.TryGetProperty(KubernetesContainerProbePayloadProperties.Exec, out var execElement))
                handler.Exec = ParseExecAction(execElement);

            if (handlerElement.TryGetProperty(KubernetesContainerProbePayloadProperties.HttpGet, out var httpGetElement))
                handler.HttpGet = ParseHttpGetAction(httpGetElement);

            if (handlerElement.TryGetProperty(KubernetesContainerProbePayloadProperties.TcpSocket, out var tcpSocketElement))
                handler.TcpSocket = ParseTcpSocketAction(tcpSocketElement);
        }

        if (handler.Exec == null && handler.HttpGet == null && handler.TcpSocket == null)
            return null;

        return handler;
    }

    internal static void AppendKeyValueIfNotNullOrWhiteSpace(StringBuilder sb, string indent, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        sb.Append(indent);
        sb.Append(YamlSafeScalar.Escape(key));
        sb.Append(": ");
        sb.AppendLine(YamlSafeScalar.Escape(value));
    }

    internal static void AppendBoolValueIfNotNullOrWhiteSpace(StringBuilder sb, string indent, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        if (bool.TryParse(value, out var b))
            sb.AppendLine($"{indent}{key}: {(b ? "true" : "false")}");
    }

    internal static void AppendIntValueIfNotNullOrWhiteSpace(StringBuilder sb, string indent, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        if (int.TryParse(value, out _))
            sb.AppendLine($"{indent}{key}: {value}");
    }

    internal static void AppendIntOrStringValue(StringBuilder sb, string indent, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        sb.AppendLine(int.TryParse(value, out _)
            ? $"{indent}{key}: {value}"
            : $"{indent}{key}: {YamlSafeScalar.Escape(value)}");
    }

    internal static void AppendDataValue(StringBuilder sb, string indent, string key, string value)
    {
        var escapedKey = YamlSafeScalar.Escape(key);

        if (value.Contains('\n', StringComparison.Ordinal))
        {
            var innerIndent = indent + "  ";
            var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
            var indented = normalized.Replace("\n", "\n" + innerIndent, StringComparison.Ordinal);
            sb.AppendLine($"{indent}{escapedKey}: |");
            sb.AppendLine(innerIndent + indented);
            return;
        }

        sb.Append($"{indent}{escapedKey}: ");
        AppendYamlString(sb, value);
        sb.AppendLine();
    }

    internal static void AppendProbeYaml(StringBuilder sb, string indent, string name, ProbeSpec probe)
    {
        sb.Append(indent);
        sb.AppendLine($"{name}:");

        var innerIndent = indent + "  ";

        AppendIntValueIfNotNullOrWhiteSpace(sb, innerIndent, KubernetesContainerProbePayloadProperties.FailureThreshold, probe.FailureThreshold);
        AppendIntValueIfNotNullOrWhiteSpace(sb, innerIndent, KubernetesContainerProbePayloadProperties.InitialDelaySeconds, probe.InitialDelaySeconds);
        AppendIntValueIfNotNullOrWhiteSpace(sb, innerIndent, KubernetesContainerProbePayloadProperties.PeriodSeconds, probe.PeriodSeconds);
        AppendIntValueIfNotNullOrWhiteSpace(sb, innerIndent, KubernetesContainerProbePayloadProperties.SuccessThreshold, probe.SuccessThreshold);
        AppendIntValueIfNotNullOrWhiteSpace(sb, innerIndent, KubernetesContainerProbePayloadProperties.TimeoutSeconds, probe.TimeoutSeconds);

        if (probe.Exec != null && probe.Exec.Command.Count > 0)
        {
            sb.Append(innerIndent);
            sb.AppendLine($"{KubernetesContainerProbePayloadProperties.Exec}:");
            sb.Append(innerIndent);
            sb.AppendLine($"  {KubernetesProbeActionPayloadProperties.Command}:");

            foreach (var command in probe.Exec.Command)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                sb.Append(innerIndent);
                sb.AppendLine($"  - {YamlSafeScalar.Escape(command)}");
            }
        }

        if (probe.HttpGet != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine($"{KubernetesContainerProbePayloadProperties.HttpGet}:");

            var httpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, KubernetesProbeActionPayloadProperties.Host, probe.HttpGet.Host);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, KubernetesProbeActionPayloadProperties.Path, probe.HttpGet.Path);
            AppendIntOrStringValue(sb, httpIndent, KubernetesProbeActionPayloadProperties.Port, probe.HttpGet.Port);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, KubernetesProbeActionPayloadProperties.Scheme, probe.HttpGet.Scheme);

            if (probe.HttpGet.HttpHeaders.Count > 0)
            {
                sb.Append(httpIndent);
                sb.AppendLine($"{KubernetesProbeActionPayloadProperties.HttpHeaders}:");

                foreach (var header in probe.HttpGet.HttpHeaders)
                {
                    if (string.IsNullOrWhiteSpace(header.Name))
                        continue;

                    sb.Append(httpIndent);
                    sb.AppendLine($"  - {KubernetesProbeActionPayloadProperties.Name}: {YamlSafeScalar.Escape(header.Name)}");

                    if (!string.IsNullOrWhiteSpace(header.Value))
                    {
                        sb.Append(httpIndent);
                        sb.AppendLine($"    {KubernetesProbeActionPayloadProperties.Value}: {YamlSafeScalar.Escape(header.Value)}");
                    }
                }
            }
        }

        if (probe.TcpSocket != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine($"{KubernetesContainerProbePayloadProperties.TcpSocket}:");

            var tcpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, tcpIndent, KubernetesProbeActionPayloadProperties.Host, probe.TcpSocket.Host);
            AppendIntOrStringValue(sb, tcpIndent, KubernetesProbeActionPayloadProperties.Port, probe.TcpSocket.Port);
        }
    }

    internal static void AppendLifecycleHandlerYaml(StringBuilder sb, string indent, string name, LifecycleHandlerSpec handler)
    {
        sb.Append(indent);
        sb.AppendLine($"{name}:");

        var innerIndent = indent + "  ";

        if (handler.Exec != null && handler.Exec.Command.Count > 0)
        {
            sb.Append(innerIndent);
            sb.AppendLine($"{KubernetesContainerProbePayloadProperties.Exec}:");
            sb.Append(innerIndent);
            sb.AppendLine($"  {KubernetesProbeActionPayloadProperties.Command}:");

            foreach (var command in handler.Exec.Command)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                sb.Append(innerIndent);
                sb.AppendLine($"  - {YamlSafeScalar.Escape(command)}");
            }
        }

        if (handler.HttpGet != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine($"{KubernetesContainerProbePayloadProperties.HttpGet}:");

            var httpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, KubernetesProbeActionPayloadProperties.Host, handler.HttpGet.Host);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, KubernetesProbeActionPayloadProperties.Path, handler.HttpGet.Path);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, KubernetesProbeActionPayloadProperties.Port, handler.HttpGet.Port);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, httpIndent, KubernetesProbeActionPayloadProperties.Scheme, handler.HttpGet.Scheme);

            if (handler.HttpGet.HttpHeaders.Count > 0)
            {
                sb.Append(httpIndent);
                sb.AppendLine($"{KubernetesProbeActionPayloadProperties.HttpHeaders}:");

                foreach (var header in handler.HttpGet.HttpHeaders)
                {
                    if (string.IsNullOrWhiteSpace(header.Name))
                        continue;

                    sb.Append(httpIndent);
                    sb.AppendLine($"  - {KubernetesProbeActionPayloadProperties.Name}: {YamlSafeScalar.Escape(header.Name)}");

                    if (!string.IsNullOrWhiteSpace(header.Value))
                    {
                        sb.Append(httpIndent);
                        sb.AppendLine($"    {KubernetesProbeActionPayloadProperties.Value}: {YamlSafeScalar.Escape(header.Value)}");
                    }
                }
            }
        }

        if (handler.TcpSocket != null)
        {
            sb.Append(innerIndent);
            sb.AppendLine($"{KubernetesContainerProbePayloadProperties.TcpSocket}:");

            var tcpIndent = innerIndent + "  ";

            AppendKeyValueIfNotNullOrWhiteSpace(sb, tcpIndent, KubernetesProbeActionPayloadProperties.Host, handler.TcpSocket.Host);
            AppendKeyValueIfNotNullOrWhiteSpace(sb, tcpIndent, KubernetesProbeActionPayloadProperties.Port, handler.TcpSocket.Port);
        }
    }

    internal static void AppendJsonFromProperty(StringBuilder sb, string indent, string key, Dictionary<string, string> properties, string propertyName)
    {
        if (!properties.TryGetValue(propertyName, out var raw) || string.IsNullOrWhiteSpace(raw))
            return;

        raw = raw.Trim();

        if (raw == "[]" || raw == "{}")
            return;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            sb.Append(indent);
            sb.AppendLine($"{key}:");

            AppendJsonElementYaml(sb, indent + "  ", doc.RootElement);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse JSON property {Key}", key);
        }
    }

    internal static void AppendJsonElementYaml(StringBuilder sb, string indent, JsonElement element)
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
                        AppendObjectArrayItem(sb, indent, item);
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
        if (element.ValueKind == JsonValueKind.String)
            AppendYamlString(sb, element.GetString() ?? string.Empty);
        else
            sb.Append(element.GetRawText());
    }

    private static void AppendYamlString(StringBuilder sb, string value)
    {
        if (value.Length == 0)
        {
            sb.Append("''");
            return;
        }

        if (NeedsYamlQuoting(value))
        {
            sb.Append('\'');
            sb.Append(value.Replace("'", "''", StringComparison.Ordinal));
            sb.Append('\'');
            return;
        }

        sb.Append(value);
    }

    private static bool NeedsYamlQuoting(string value)
    {
        var first = value[0];

        if (first is ':' or '{' or '[' or ',' or '&' or '*' or '?' or '|'
                or '>' or '!' or '%' or '@' or '`' or '\'' or '"' or '#' or '-')
            return true;

        var lower = value.ToLowerInvariant();

        if (lower is "true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~")
            return true;

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return true;

        if (value.Contains(": ", StringComparison.Ordinal) || value.EndsWith(":", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static void AppendObjectArrayItem(StringBuilder sb, string indent, JsonElement obj)
    {
        var subIndent = indent + "  ";
        var first = true;

        foreach (var property in obj.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;

            if (first)
            {
                sb.Append(indent);
                sb.Append("- ");
                first = false;
            }
            else
            {
                sb.Append(subIndent);
            }

            if (value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine($"{name}:");
                AppendJsonElementYaml(sb, subIndent + "  ", value);
            }
            else
            {
                sb.Append($"{name}: ");
                AppendJsonScalarValue(sb, value);
                sb.AppendLine();
            }
        }

        if (first)
        {
            sb.Append(indent);
            sb.AppendLine("- {}");
        }
    }
}

internal sealed class ServicePortSpec
{
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? TargetPort { get; set; }
    public int? NodePort { get; set; }
    public string Protocol { get; set; } = string.Empty;
}

internal sealed class ContainerSpec
{
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public List<ContainerPortSpec> Ports { get; } = new();
    public Dictionary<string, string> ResourcesRequests { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResourcesLimits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VolumeMountSpec> VolumeMounts { get; } = new();
    public List<EnvFromSpec> ConfigMapEnvFromSource { get; } = new();
    public ProbeSpec? LivenessProbe { get; set; }
    public ProbeSpec? ReadinessProbe { get; set; }
    public ProbeSpec? StartupProbe { get; set; }
    public SecurityContextSpec? SecurityContext { get; set; }
    public LifecycleSpec? Lifecycle { get; set; }
    public bool IsInitContainer { get; set; }

    // Environment variables
    public List<EnvVarSpec> EnvironmentVariables { get; } = new();
    public List<EnvVarSourceSpec> ConfigMapEnvVariables { get; } = new();
    public List<EnvVarSourceSpec> SecretEnvVariables { get; } = new();
    public List<FieldRefEnvVarSpec> FieldRefEnvVariables { get; } = new();
    public List<EnvFromSpec> SecretEnvFromSource { get; } = new();

    // Container settings
    public string? ImagePullPolicy { get; set; }
    public string? TerminationMessagePath { get; set; }
    public string? TerminationMessagePolicy { get; set; }
    public List<string> Command { get; } = new();
    public List<string> Args { get; } = new();
}

internal sealed class ContainerPortSpec
{
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Protocol { get; set; } = string.Empty;
}

internal sealed class VolumeSpec
{
    public string Name { get; set; } = string.Empty;
    public string? ConfigMapName { get; set; }
    public string? SecretName { get; set; }
    public bool EmptyDir { get; set; }
    public string? PvcClaimName { get; set; }
    public string? HostPath { get; set; }
}

internal sealed class ProbeSpec
{
    public string? FailureThreshold { get; set; }
    public string? InitialDelaySeconds { get; set; }
    public string? PeriodSeconds { get; set; }
    public string? SuccessThreshold { get; set; }
    public string? TimeoutSeconds { get; set; }
    public ExecActionSpec? Exec { get; set; }
    public HttpGetActionSpec? HttpGet { get; set; }
    public TcpSocketActionSpec? TcpSocket { get; set; }
}

internal sealed class ExecActionSpec
{
    public List<string> Command { get; } = new();
}

internal sealed class HttpGetActionSpec
{
    public string? Host { get; set; }
    public string? Path { get; set; }
    public string? Port { get; set; }
    public string? Scheme { get; set; }
    public List<HttpHeaderSpec> HttpHeaders { get; } = new();
}

internal sealed class HttpHeaderSpec
{
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
}

internal sealed class TcpSocketActionSpec
{
    public string? Host { get; set; }
    public string? Port { get; set; }
}

internal sealed class SecurityContextSpec
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

internal sealed class CapabilitiesSpec
{
    public List<string> Add { get; } = new();
    public List<string> Drop { get; } = new();
}

internal sealed class SeLinuxOptionsSpec
{
    public string? Level { get; set; }
    public string? Role { get; set; }
    public string? Type { get; set; }
    public string? User { get; set; }
}

internal sealed class LifecycleSpec
{
    public LifecycleHandlerSpec? PreStop { get; set; }
    public LifecycleHandlerSpec? PostStart { get; set; }
}

internal sealed class LifecycleHandlerSpec
{
    public ExecActionSpec? Exec { get; set; }
    public HttpGetActionSpec? HttpGet { get; set; }
    public TcpSocketActionSpec? TcpSocket { get; set; }
}

internal sealed class VolumeMountSpec
{
    public string Name { get; set; } = string.Empty;
    public string MountPath { get; set; } = string.Empty;
    public string? SubPath { get; set; }
}

internal sealed class EnvVarSpec
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

internal sealed class EnvVarSourceSpec
{
    public string EnvVarName { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public bool? Optional { get; set; }
}

internal sealed class EnvFromSpec
{
    public string Name { get; set; } = string.Empty;
    public string? Prefix { get; set; }
    public bool? Optional { get; set; }
}

internal sealed class FieldRefEnvVarSpec
{
    public string EnvVarName { get; set; } = string.Empty;
    public string FieldPath { get; set; } = string.Empty;
}
