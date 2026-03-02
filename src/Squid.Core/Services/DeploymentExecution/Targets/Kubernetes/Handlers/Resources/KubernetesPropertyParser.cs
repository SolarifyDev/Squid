using System.Globalization;
using System.Text;
using System.Text.Json;
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
        var ns = GetProperty(properties, "Squid.Action.KubernetesContainers.Namespace");

        if (string.IsNullOrWhiteSpace(ns))
            ns = GetProperty(properties, "Squid.Action.Kubernetes.Namespace");

        if (string.IsNullOrWhiteSpace(ns))
            ns = "default";

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

                    var key = element.TryGetProperty("Key", out var keyProp) ? keyProp.GetString()
                        : element.TryGetProperty("key", out var lowerKeyProp) ? lowerKeyProp.GetString()
                        : null;

                    var value = element.TryGetProperty("Value", out var valueProp) ? valueProp.GetString()
                        : element.TryGetProperty("value", out var lowerValueProp) ? lowerValueProp.GetString()
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
        catch
        {
        }

        return result;
    }

    internal static List<ServicePortSpec> ParseServicePorts(Dictionary<string, string> properties)
    {
        var result = new List<ServicePortSpec>();

        var portsJson = GetProperty(properties, "Squid.Action.KubernetesContainers.ServicePorts");

        if (string.IsNullOrWhiteSpace(portsJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(portsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var portText = element.TryGetProperty("port", out var portProp) ? portProp.GetString() : null;
                var targetPortText = element.TryGetProperty("targetPort", out var targetPortProp) ? targetPortProp.GetString() : null;
                var nodePortText = element.TryGetProperty("nodePort", out var nodePortProp) ? nodePortProp.GetString() : null;
                var protocol = element.TryGetProperty("protocol", out var protocolProp) ? protocolProp.GetString() ?? string.Empty : string.Empty;

                if (!int.TryParse(portText, out var port))
                    continue;

                int? targetPort = null;

                if (int.TryParse(targetPortText, out var parsedTargetPort))
                    targetPort = parsedTargetPort;

                int? nodePort = null;

                if (int.TryParse(nodePortText, out var parsedNodePort))
                    nodePort = parsedNodePort;

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

    internal static List<ContainerSpec> ParseContainers(Dictionary<string, string> properties)
    {
        var result = new List<ContainerSpec>();

        var containersJson = GetProperty(properties, "Squid.Action.KubernetesContainers.Containers");

        if (string.IsNullOrWhiteSpace(containersJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(containersJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

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

                container.IsInitContainer = element.TryGetProperty("IsInitContainer", out var initProp)
                    && string.Equals(initProp.GetString(), "True", StringComparison.OrdinalIgnoreCase);

                result.Add(container);
            }
        }
        catch
        {
        }

        return result;
    }

    internal static List<VolumeSpec> ParseVolumes(Dictionary<string, string> properties)
    {
        var result = new List<VolumeSpec>();

        var combinedVolumesJson = GetProperty(properties, "Squid.Action.KubernetesContainers.CombinedVolumes");

        if (string.IsNullOrWhiteSpace(combinedVolumesJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(combinedVolumesJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var type = element.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
                var referenceName = element.TryGetProperty("ReferenceName", out var referenceProp) ? referenceProp.GetString() ?? string.Empty : string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var volume = new VolumeSpec { Name = name };

                if (string.Equals(type, "ConfigMap", StringComparison.OrdinalIgnoreCase))
                    volume.ConfigMapName = referenceName;
                else if (string.Equals(type, "Secret", StringComparison.OrdinalIgnoreCase))
                    volume.SecretName = referenceName;
                else if (string.Equals(type, "EmptyDir", StringComparison.OrdinalIgnoreCase))
                    volume.EmptyDir = true;
                else if (string.Equals(type, "PVC", StringComparison.OrdinalIgnoreCase))
                    volume.PvcClaimName = referenceName;
                else if (string.Equals(type, "HostPath", StringComparison.OrdinalIgnoreCase))
                    volume.HostPath = referenceName;

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
            return imageProp.GetString() ?? string.Empty;

        if (containerElement.TryGetProperty("PackageId", out var packageIdProp))
        {
            var packageId = packageIdProp.GetString();

            if (!string.IsNullOrWhiteSpace(packageId))
                return packageId;
        }

        return null;
    }

    private static void FillContainerPorts(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("Ports", out var portsElement) || portsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var portElement in portsElement.EnumerateArray())
        {
            var name = portElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var portText = portElement.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
            var protocol = portElement.TryGetProperty("option", out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;

            if (!int.TryParse(portText, out var port))
                continue;

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
            return;

        if (resourcesElement.TryGetProperty("requests", out var requestsElement) && requestsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in requestsElement.EnumerateObject())
            {
                var value = property.Value.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                    container.ResourcesRequests[property.Name] = value;
            }
        }

        if (resourcesElement.TryGetProperty("limits", out var limitsElement) && limitsElement.ValueKind == JsonValueKind.Object)
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
        if (!element.TryGetProperty("VolumeMounts", out var mountsElement) || mountsElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var mountElement in mountsElement.EnumerateArray())
        {
            var name = mountElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;
            var mountPath = mountElement.TryGetProperty("value", out var valueProp) ? valueProp.GetString() ?? string.Empty : string.Empty;
            var subPath = mountElement.TryGetProperty("option", out var optionProp) ? optionProp.GetString() ?? string.Empty : string.Empty;

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
        if (!element.TryGetProperty("ConfigMapEnvFromSource", out var envFromElement) || envFromElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemElement in envFromElement.EnumerateArray())
        {
            var name = itemElement.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? string.Empty : string.Empty;

            if (!string.IsNullOrWhiteSpace(name))
                container.ConfigMapEnvFromSource.Add(name);
        }
    }

    private static void FillContainerProbes(JsonElement element, ContainerSpec container)
    {
        if (element.TryGetProperty("LivenessProbe", out var livenessElement))
        {
            var probe = ParseProbe(livenessElement);

            if (probe != null)
                container.LivenessProbe = probe;
        }

        if (element.TryGetProperty("ReadinessProbe", out var readinessElement))
        {
            var probe = ParseProbe(readinessElement);

            if (probe != null)
                container.ReadinessProbe = probe;
        }

        if (element.TryGetProperty("StartupProbe", out var startupElement))
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
            FailureThreshold = GetOptionalString(probeElement, "failureThreshold"),
            InitialDelaySeconds = GetOptionalString(probeElement, "initialDelaySeconds"),
            PeriodSeconds = GetOptionalString(probeElement, "periodSeconds"),
            SuccessThreshold = GetOptionalString(probeElement, "successThreshold"),
            TimeoutSeconds = GetOptionalString(probeElement, "timeoutSeconds")
        };

        if (probeElement.TryGetProperty("exec", out var execElement))
            result.Exec = ParseExecAction(execElement);

        if (probeElement.TryGetProperty("httpGet", out var httpGetElement))
            result.HttpGet = ParseHttpGetAction(httpGetElement);

        if (probeElement.TryGetProperty("tcpSocket", out var tcpSocketElement))
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

        if (!execElement.TryGetProperty("command", out var commandElement))
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
                    continue;

                var name = headerElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var value = headerElement.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;

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
            Host = GetOptionalString(tcpSocketElement, "host"),
            Port = GetOptionalString(tcpSocketElement, "port")
        };

        if (string.IsNullOrWhiteSpace(result.Host) && string.IsNullOrWhiteSpace(result.Port))
            return null;

        return result;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }

    private static void FillContainerSecurityContext(JsonElement element, ContainerSpec container)
    {
        if (!element.TryGetProperty("SecurityContext", out var contextElement) || contextElement.ValueKind != JsonValueKind.Object)
            return;

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
                        capabilities.Add.Add(value);
                }
            }

            if (capabilitiesElement.TryGetProperty("drop", out var dropElement) && dropElement.ValueKind == JsonValueKind.Array)
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
            return;

        var lifecycle = new LifecycleSpec
        {
            PreStop = ParseLifecycleHandler(lifecycleElement, "preStop"),
            PostStart = ParseLifecycleHandler(lifecycleElement, "postStart")
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
        var type = GetOptionalString(handlerElement, "type") ?? string.Empty;

        if (string.Equals(type, "exec", StringComparison.OrdinalIgnoreCase))
            handler.Exec = ParseExecAction(handlerElement);
        else if (string.Equals(type, "httpGet", StringComparison.OrdinalIgnoreCase))
            handler.HttpGet = ParseHttpGetAction(handlerElement);
        else if (string.Equals(type, "tcpSocket", StringComparison.OrdinalIgnoreCase))
            handler.TcpSocket = ParseTcpSocketAction(handlerElement);
        else
        {
            if (handlerElement.TryGetProperty("exec", out var execElement))
                handler.Exec = ParseExecAction(execElement);

            if (handlerElement.TryGetProperty("httpGet", out var httpGetElement))
                handler.HttpGet = ParseHttpGetAction(httpGetElement);

            if (handlerElement.TryGetProperty("tcpSocket", out var tcpSocketElement))
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
        sb.Append(key);
        sb.Append(": ");
        sb.AppendLine(value);
    }

    internal static void AppendProbeYaml(StringBuilder sb, string indent, string name, ProbeSpec probe)
    {
        sb.Append(indent);
        sb.AppendLine($"{name}:");

        var innerIndent = indent + "  ";

        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "failureThreshold", probe.FailureThreshold);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "initialDelaySeconds", probe.InitialDelaySeconds);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "periodSeconds", probe.PeriodSeconds);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "successThreshold", probe.SuccessThreshold);
        AppendKeyValueIfNotNullOrWhiteSpace(sb, innerIndent, "timeoutSeconds", probe.TimeoutSeconds);

        if (probe.Exec != null && probe.Exec.Command.Count > 0)
        {
            sb.Append(innerIndent);
            sb.AppendLine("exec:");
            sb.Append(innerIndent);
            sb.AppendLine("  command:");

            foreach (var command in probe.Exec.Command)
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;

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
                        continue;

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

    internal static void AppendLifecycleHandlerYaml(StringBuilder sb, string indent, string name, LifecycleHandlerSpec handler)
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
                    continue;

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
                        continue;

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
        catch
        {
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
    public int? TargetPort { get; set; }
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
    public List<string> ConfigMapEnvFromSource { get; } = new();
    public ProbeSpec? LivenessProbe { get; set; }
    public ProbeSpec? ReadinessProbe { get; set; }
    public ProbeSpec? StartupProbe { get; set; }
    public SecurityContextSpec? SecurityContext { get; set; }
    public LifecycleSpec? Lifecycle { get; set; }
    public bool IsInitContainer { get; set; }
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
