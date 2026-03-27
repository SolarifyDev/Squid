using System.Text;
using System.Text.Json;
using Serilog;
using Squid.Core.Services.DeploymentExecution.Infrastructure;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class PodTemplateYamlBuilder
{
    internal static void AppendPodTemplate(StringBuilder sb, Dictionary<string, string> properties, Dictionary<string, string> selectorLabels, string baseIndent)
    {
        var containerSpecs = KubernetesPropertyParser.ParseContainers(properties);
        var volumes = KubernetesPropertyParser.ParseVolumes(properties);
        var podAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.PodAnnotations);

        InjectDeployIdAnnotation(podAnnotations, properties);

        var templateIndent = baseIndent + "  ";
        var metadataIndent = templateIndent + "  ";
        var labelsIndent = metadataIndent + "  ";
        var specIndent = templateIndent + "  ";

        sb.AppendLine($"{baseIndent}template:");
        sb.AppendLine($"{templateIndent}metadata:");
        sb.AppendLine($"{metadataIndent}labels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, labelsIndent, kvp.Key, kvp.Value);

        AppendDictionary(sb, $"{metadataIndent}annotations:", labelsIndent, podAnnotations);

        sb.AppendLine($"{templateIndent}spec:");
        AppendStringPropertyIfPresent(sb, specIndent, "serviceAccountName", properties, KubernetesProperties.ServiceAccountName);
        AppendRestartPolicyIfNeeded(sb, specIndent, properties);
        AppendDnsPolicyIfNeeded(sb, specIndent, properties);
        AppendHostNetworkIfNeeded(sb, specIndent, properties);
        AppendIntPropertyIfPresent(sb, specIndent, "terminationGracePeriodSeconds", properties, KubernetesProperties.PodTerminationGracePeriodSeconds);
        AppendStringPropertyIfPresent(sb, specIndent, "priorityClassName", properties, KubernetesProperties.PodPriorityClassName);
        AppendReadinessGatesIfPresent(sb, specIndent, properties);

        if (volumes.Count > 0)
        {
            sb.AppendLine($"{specIndent}volumes:");

            foreach (var volume in volumes)
                AppendVolumeYaml(sb, specIndent, volume);
        }

        AppendTolerationsIfPresent(sb, specIndent, properties);

        AppendAffinityIfPresent(sb, specIndent, properties);
        AppendDnsConfigIfPresent(sb, specIndent, properties);
        AppendPodSecurityContextIfPresent(sb, specIndent, properties);
        AppendImagePullSecretsIfPresent(sb, specIndent, properties);
        AppendHostAliasesIfPresent(sb, specIndent, properties);

        var initContainers = containerSpecs.Where(c => c.IsInitContainer).ToList();
        var regularContainers = containerSpecs.Where(c => !c.IsInitContainer).ToList();

        if (initContainers.Count > 0)
        {
            sb.AppendLine($"{specIndent}initContainers:");

            foreach (var container in initContainers)
                AppendContainerYaml(sb, specIndent, container);
        }

        sb.AppendLine($"{specIndent}containers:");

        foreach (var container in regularContainers)
            AppendContainerYaml(sb, specIndent, container);
    }

    private static void InjectDeployIdAnnotation(Dictionary<string, string> podAnnotations, Dictionary<string, string> properties)
    {
        var suffix = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentIdSuffix);
        if (string.IsNullOrWhiteSpace(suffix)) return;

        podAnnotations["squid.io/deploy-id"] = suffix;
    }

    internal static void AppendContainerYaml(StringBuilder sb, string specIndent, ContainerSpec container)
    {
        var indent = specIndent + "  ";

        sb.AppendLine($"{specIndent}- name: {YamlSafeScalar.Escape(container.Name)}");
        sb.AppendLine($"{indent}image: {YamlSafeScalar.Escape(container.Image)}");

        if (!string.IsNullOrWhiteSpace(container.ImagePullPolicy))
            sb.AppendLine($"{indent}imagePullPolicy: {YamlSafeScalar.Escape(container.ImagePullPolicy)}");

        if (container.Ports.Count > 0)
        {
            sb.AppendLine($"{indent}ports:");

            foreach (var port in container.Ports)
            {
                sb.AppendLine($"{indent}- name: {YamlSafeScalar.Escape(port.Name)}");
                sb.AppendLine($"{indent}  containerPort: {port.Port}");

                if (!string.IsNullOrWhiteSpace(port.Protocol))
                    sb.AppendLine($"{indent}  protocol: {YamlSafeScalar.Escape(port.Protocol)}");
            }
        }

        if (container.ResourcesRequests.Count > 0 || container.ResourcesLimits.Count > 0)
        {
            sb.AppendLine($"{indent}resources:");

            if (container.ResourcesRequests.Count > 0)
            {
                sb.AppendLine($"{indent}  requests:");

                foreach (var kvp in container.ResourcesRequests)
                    sb.AppendLine($"{indent}    {YamlSafeScalar.Escape(kvp.Key)}: {YamlSafeScalar.Escape(kvp.Value)}");
            }

            if (container.ResourcesLimits.Count > 0)
            {
                sb.AppendLine($"{indent}  limits:");

                foreach (var kvp in container.ResourcesLimits)
                    sb.AppendLine($"{indent}    {YamlSafeScalar.Escape(kvp.Key)}: {YamlSafeScalar.Escape(kvp.Value)}");
            }
        }

        if (container.VolumeMounts.Count > 0)
        {
            sb.AppendLine($"{indent}volumeMounts:");

            foreach (var mount in container.VolumeMounts)
            {
                sb.AppendLine($"{indent}- name: {YamlSafeScalar.Escape(mount.Name)}");
                sb.AppendLine($"{indent}  mountPath: {YamlSafeScalar.Escape(mount.MountPath)}");

                if (!string.IsNullOrWhiteSpace(mount.SubPath))
                    sb.AppendLine($"{indent}  subPath: {YamlSafeScalar.Escape(mount.SubPath)}");
            }
        }

        if (container.Command.Count > 0)
        {
            sb.AppendLine($"{indent}command:");

            foreach (var cmd in container.Command)
                sb.AppendLine($"{indent}- {YamlSafeScalar.Escape(cmd)}");
        }

        if (container.Args.Count > 0)
        {
            sb.AppendLine($"{indent}args:");

            foreach (var arg in container.Args)
                sb.AppendLine($"{indent}- {YamlSafeScalar.Escape(arg)}");
        }

        if (container.ConfigMapEnvFromSource.Count > 0 || container.SecretEnvFromSource.Count > 0)
        {
            sb.AppendLine($"{indent}envFrom:");

            foreach (var envFrom in container.ConfigMapEnvFromSource)
            {
                sb.AppendLine($"{indent}- configMapRef:");
                sb.AppendLine($"{indent}    name: {YamlSafeScalar.Escape(envFrom.Name)}");

                if (envFrom.Optional.HasValue)
                    sb.AppendLine($"{indent}    optional: {(envFrom.Optional.Value ? "true" : "false")}");

                if (!string.IsNullOrWhiteSpace(envFrom.Prefix))
                    sb.AppendLine($"{indent}  prefix: {YamlSafeScalar.Escape(envFrom.Prefix)}");
            }

            foreach (var envFrom in container.SecretEnvFromSource)
            {
                sb.AppendLine($"{indent}- secretRef:");
                sb.AppendLine($"{indent}    name: {YamlSafeScalar.Escape(envFrom.Name)}");

                if (envFrom.Optional.HasValue)
                    sb.AppendLine($"{indent}    optional: {(envFrom.Optional.Value ? "true" : "false")}");

                if (!string.IsNullOrWhiteSpace(envFrom.Prefix))
                    sb.AppendLine($"{indent}  prefix: {YamlSafeScalar.Escape(envFrom.Prefix)}");
            }
        }

        var hasEnv = container.EnvironmentVariables.Count > 0
            || container.ConfigMapEnvVariables.Count > 0
            || container.SecretEnvVariables.Count > 0
            || container.FieldRefEnvVariables.Count > 0;

        if (hasEnv)
        {
            sb.AppendLine($"{indent}env:");

            foreach (var env in container.EnvironmentVariables)
            {
                sb.AppendLine($"{indent}- name: {YamlSafeScalar.Escape(env.Name)}");
                KubernetesPropertyParser.AppendDataValue(sb, $"{indent}  ", "value", env.Value);
            }

            foreach (var env in container.ConfigMapEnvVariables)
            {
                sb.AppendLine($"{indent}- name: {YamlSafeScalar.Escape(env.EnvVarName)}");
                sb.AppendLine($"{indent}  valueFrom:");
                sb.AppendLine($"{indent}    configMapKeyRef:");
                sb.AppendLine($"{indent}      name: {YamlSafeScalar.Escape(env.SourceName)}");
                sb.AppendLine($"{indent}      key: {YamlSafeScalar.Escape(env.SourceKey)}");

                if (env.Optional.HasValue)
                    sb.AppendLine($"{indent}      optional: {(env.Optional.Value ? "true" : "false")}");
            }

            foreach (var env in container.SecretEnvVariables)
            {
                sb.AppendLine($"{indent}- name: {YamlSafeScalar.Escape(env.EnvVarName)}");
                sb.AppendLine($"{indent}  valueFrom:");
                sb.AppendLine($"{indent}    secretKeyRef:");
                sb.AppendLine($"{indent}      name: {YamlSafeScalar.Escape(env.SourceName)}");
                sb.AppendLine($"{indent}      key: {YamlSafeScalar.Escape(env.SourceKey)}");

                if (env.Optional.HasValue)
                    sb.AppendLine($"{indent}      optional: {(env.Optional.Value ? "true" : "false")}");
            }

            foreach (var env in container.FieldRefEnvVariables)
            {
                sb.AppendLine($"{indent}- name: {YamlSafeScalar.Escape(env.EnvVarName)}");
                sb.AppendLine($"{indent}  valueFrom:");
                sb.AppendLine($"{indent}    fieldRef:");
                sb.AppendLine($"{indent}      fieldPath: {YamlSafeScalar.Escape(env.FieldPath)}");
            }
        }

        if (container.LivenessProbe != null)
            KubernetesPropertyParser.AppendProbeYaml(sb, indent, "livenessProbe", container.LivenessProbe);

        if (container.ReadinessProbe != null)
            KubernetesPropertyParser.AppendProbeYaml(sb, indent, "readinessProbe", container.ReadinessProbe);

        if (container.StartupProbe != null)
            KubernetesPropertyParser.AppendProbeYaml(sb, indent, "startupProbe", container.StartupProbe);

        if (container.Lifecycle != null)
        {
            sb.AppendLine($"{indent}lifecycle:");

            if (container.Lifecycle.PreStop != null)
                KubernetesPropertyParser.AppendLifecycleHandlerYaml(sb, $"{indent}  ", "preStop", container.Lifecycle.PreStop);

            if (container.Lifecycle.PostStart != null)
                KubernetesPropertyParser.AppendLifecycleHandlerYaml(sb, $"{indent}  ", "postStart", container.Lifecycle.PostStart);
        }

        if (!string.IsNullOrWhiteSpace(container.TerminationMessagePath))
            sb.AppendLine($"{indent}terminationMessagePath: {YamlSafeScalar.Escape(container.TerminationMessagePath)}");

        if (!string.IsNullOrWhiteSpace(container.TerminationMessagePolicy))
            sb.AppendLine($"{indent}terminationMessagePolicy: {YamlSafeScalar.Escape(container.TerminationMessagePolicy)}");

        if (container.SecurityContext != null)
        {
            sb.AppendLine($"{indent}securityContext:");

            var scIndent = $"{indent}  ";

            KubernetesPropertyParser.AppendBoolValueIfNotNullOrWhiteSpace(sb, scIndent, KubernetesContainerSecurityContextPayloadProperties.AllowPrivilegeEscalation, container.SecurityContext.AllowPrivilegeEscalation);
            KubernetesPropertyParser.AppendBoolValueIfNotNullOrWhiteSpace(sb, scIndent, KubernetesContainerSecurityContextPayloadProperties.Privileged, container.SecurityContext.Privileged);
            KubernetesPropertyParser.AppendBoolValueIfNotNullOrWhiteSpace(sb, scIndent, KubernetesContainerSecurityContextPayloadProperties.ReadOnlyRootFilesystem, container.SecurityContext.ReadOnlyRootFilesystem);
            KubernetesPropertyParser.AppendIntValueIfNotNullOrWhiteSpace(sb, scIndent, KubernetesContainerSecurityContextPayloadProperties.RunAsGroup, container.SecurityContext.RunAsGroup);
            KubernetesPropertyParser.AppendBoolValueIfNotNullOrWhiteSpace(sb, scIndent, KubernetesContainerSecurityContextPayloadProperties.RunAsNonRoot, container.SecurityContext.RunAsNonRoot);
            KubernetesPropertyParser.AppendIntValueIfNotNullOrWhiteSpace(sb, scIndent, KubernetesContainerSecurityContextPayloadProperties.RunAsUser, container.SecurityContext.RunAsUser);

            if (container.SecurityContext.Capabilities != null
                && (container.SecurityContext.Capabilities.Add.Count > 0 || container.SecurityContext.Capabilities.Drop.Count > 0))
            {
                sb.AppendLine($"{scIndent}{KubernetesSecurityContextPayloadProperties.Capabilities}:");

                if (container.SecurityContext.Capabilities.Add.Count > 0)
                {
                    sb.AppendLine($"{scIndent}  {KubernetesSecurityContextPayloadProperties.Add}:");

                    foreach (var capability in container.SecurityContext.Capabilities.Add)
                    {
                        if (!string.IsNullOrWhiteSpace(capability))
                            sb.AppendLine($"{scIndent}  - {YamlSafeScalar.Escape(capability)}");
                    }
                }

                if (container.SecurityContext.Capabilities.Drop.Count > 0)
                {
                    sb.AppendLine($"{scIndent}  {KubernetesSecurityContextPayloadProperties.Drop}:");

                    foreach (var capability in container.SecurityContext.Capabilities.Drop)
                    {
                        if (!string.IsNullOrWhiteSpace(capability))
                            sb.AppendLine($"{scIndent}  - {YamlSafeScalar.Escape(capability)}");
                    }
                }
            }

            if (container.SecurityContext.SeLinuxOptions != null)
            {
                sb.AppendLine($"{scIndent}{KubernetesSecurityContextPayloadProperties.SeLinuxOptions}:");

                var seIndent = $"{scIndent}  ";

                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.Level, container.SecurityContext.SeLinuxOptions.Level);
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.Role, container.SecurityContext.SeLinuxOptions.Role);
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.Type, container.SecurityContext.SeLinuxOptions.Type);
                KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.User, container.SecurityContext.SeLinuxOptions.User);
            }
        }
    }

    internal static void AppendVolumeYaml(StringBuilder sb, string specIndent, VolumeSpec volume)
    {
        var indent = $"{specIndent}  ";

        sb.AppendLine($"{specIndent}- name: {YamlSafeScalar.Escape(volume.Name)}");

        if (!string.IsNullOrWhiteSpace(volume.ConfigMapName))
        {
            sb.AppendLine($"{indent}configMap:");
            sb.AppendLine($"{indent}  name: {YamlSafeScalar.Escape(volume.ConfigMapName)}");
            AppendVolumeItems(sb, indent, volume.Items);
        }
        else if (!string.IsNullOrWhiteSpace(volume.SecretName))
        {
            sb.AppendLine($"{indent}secret:");
            sb.AppendLine($"{indent}  secretName: {YamlSafeScalar.Escape(volume.SecretName)}");
            AppendVolumeItems(sb, indent, volume.Items);
        }
        else if (volume.EmptyDir)
        {
            sb.AppendLine($"{indent}emptyDir: {{}}");
        }
        else if (!string.IsNullOrWhiteSpace(volume.PvcClaimName))
        {
            sb.AppendLine($"{indent}persistentVolumeClaim:");
            sb.AppendLine($"{indent}  claimName: {YamlSafeScalar.Escape(volume.PvcClaimName)}");
        }
        else if (!string.IsNullOrWhiteSpace(volume.HostPath))
        {
            sb.AppendLine($"{indent}hostPath:");
            sb.AppendLine($"{indent}  path: {YamlSafeScalar.Escape(volume.HostPath)}");
        }
    }

    private static void AppendVolumeItems(StringBuilder sb, string indent, List<VolumeItemSpec>? items)
    {
        if (items is not { Count: > 0 }) return;

        sb.AppendLine($"{indent}  items:");

        foreach (var item in items)
        {
            sb.AppendLine($"{indent}  - key: {YamlSafeScalar.Escape(item.Key)}");

            if (!string.IsNullOrWhiteSpace(item.Path))
                sb.AppendLine($"{indent}    path: {YamlSafeScalar.Escape(item.Path)}");
        }
    }

    internal static void AppendDictionary(StringBuilder sb, string header, string indent, Dictionary<string, string> dict)
    {
        if (dict.Count == 0)
            return;

        sb.AppendLine(header);

        foreach (var kvp in dict)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, indent, kvp.Key, kvp.Value);
    }

    internal static void AppendIntPropertyIfPresent(StringBuilder sb, string indent, string yamlKey, Dictionary<string, string> properties, string propertyName)
    {
        var raw = KubernetesPropertyParser.GetProperty(properties, propertyName);

        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out _))
            sb.AppendLine($"{indent}{yamlKey}: {raw}");
    }

    internal static void AppendStringPropertyIfPresent(StringBuilder sb, string indent, string yamlKey, Dictionary<string, string> properties, string propertyName)
    {
        var value = KubernetesPropertyParser.GetProperty(properties, propertyName);
        KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, indent, yamlKey, value);
    }

    private static void AppendRestartPolicyIfNeeded(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        var restartPolicy = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodRestartPolicy);

        if (string.IsNullOrWhiteSpace(restartPolicy) || string.Equals(restartPolicy, KubernetesPodDefaultValues.RestartPolicyAlways, StringComparison.OrdinalIgnoreCase))
            return;

        sb.AppendLine($"{indent}restartPolicy: {YamlSafeScalar.Escape(restartPolicy)}");
    }

    private static void AppendDnsPolicyIfNeeded(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        var dnsPolicy = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodDnsPolicy);

        if (string.IsNullOrWhiteSpace(dnsPolicy) || string.Equals(dnsPolicy, KubernetesPodDefaultValues.DnsPolicyClusterFirst, StringComparison.OrdinalIgnoreCase))
            return;

        sb.AppendLine($"{indent}dnsPolicy: {YamlSafeScalar.Escape(dnsPolicy)}");
    }

    private static void AppendHostNetworkIfNeeded(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        var raw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodHostNetworking);

        if (string.Equals(raw, KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"{indent}hostNetwork: true");
    }

    private static void AppendReadinessGatesIfPresent(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        var raw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodReadinessGates);

        if (string.IsNullOrWhiteSpace(raw))
            return;

        var gates = raw.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (gates.Length == 0)
            return;

        sb.AppendLine($"{indent}readinessGates:");

        foreach (var gate in gates)
            sb.AppendLine($"{indent}- {KubernetesReadinessGatePayloadProperties.ConditionType}: {YamlSafeScalar.Escape(gate)}");
    }

    private static void AppendTolerationsIfPresent(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue(KubernetesProperties.Tolerations, out var raw) || string.IsNullOrWhiteSpace(raw))
            return;

        raw = raw.Trim();

        if (raw == "[]")
            return;

        properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase)
        {
            [KubernetesProperties.Tolerations] = SanitizeTolerations(raw)
        };

        KubernetesPropertyParser.AppendJsonFromProperty(sb, indent, "tolerations", properties, KubernetesProperties.Tolerations);
    }

    internal static string SanitizeTolerations(string raw)
    {
        try
        {
            using var doc = KubernetesPropertyParser.SafeParseJson(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return raw;

            var needsSanitization = false;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!IsExistsOperator(item)) continue;
                if (!item.TryGetProperty("value", out var val)) continue;

                var valStr = val.ValueKind == JsonValueKind.String ? val.GetString() : val.GetRawText();

                if (!string.IsNullOrEmpty(valStr))
                {
                    needsSanitization = true;
                    break;
                }
            }

            if (!needsSanitization)
                return raw;

            var sanitized = new List<Dictionary<string, JsonElement>>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var obj = new Dictionary<string, JsonElement>();

                foreach (var prop in item.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "value", StringComparison.OrdinalIgnoreCase) && IsExistsOperator(item))
                        continue;

                    obj[prop.Name] = prop.Value.Clone();
                }

                sanitized.Add(obj);
            }

            return JsonSerializer.Serialize(sanitized);
        }
        catch
        {
            return raw;
        }
    }

    private static bool IsExistsOperator(JsonElement obj)
    {
        if (!obj.TryGetProperty("operator", out var op))
            return false;

        var opStr = op.ValueKind == JsonValueKind.String ? op.GetString() : null;

        return string.Equals(opStr, "Exists", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendAffinityIfPresent(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        var hasNodeAffinity = properties.TryGetValue(KubernetesProperties.NodeAffinity, out var nodeAffinityRaw)
            && !string.IsNullOrWhiteSpace(nodeAffinityRaw)
            && !string.Equals(nodeAffinityRaw.Trim(), KubernetesJsonLiterals.EmptyArray, StringComparison.Ordinal)
            && !string.Equals(nodeAffinityRaw.Trim(), KubernetesJsonLiterals.EmptyObject, StringComparison.Ordinal);

        var hasPodAffinity = properties.TryGetValue(KubernetesProperties.PodAffinity, out var podAffinityRaw)
            && !string.IsNullOrWhiteSpace(podAffinityRaw)
            && !string.Equals(podAffinityRaw.Trim(), KubernetesJsonLiterals.EmptyArray, StringComparison.Ordinal)
            && !string.Equals(podAffinityRaw.Trim(), KubernetesJsonLiterals.EmptyObject, StringComparison.Ordinal);

        var hasPodAntiAffinity = properties.TryGetValue(KubernetesProperties.PodAntiAffinity, out var podAntiAffinityRaw)
            && !string.IsNullOrWhiteSpace(podAntiAffinityRaw)
            && !string.Equals(podAntiAffinityRaw.Trim(), KubernetesJsonLiterals.EmptyArray, StringComparison.Ordinal)
            && !string.Equals(podAntiAffinityRaw.Trim(), KubernetesJsonLiterals.EmptyObject, StringComparison.Ordinal);

        if (!hasNodeAffinity && !hasPodAffinity && !hasPodAntiAffinity)
            return;

        var innerIndent = $"{indent}  ";

        sb.AppendLine($"{indent}affinity:");

        if (hasNodeAffinity)
        {
            try
            {
                using var doc = KubernetesPropertyParser.SafeParseJson(nodeAffinityRaw!);
                sb.AppendLine($"{innerIndent}nodeAffinity:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, $"{innerIndent}  ", doc.RootElement);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Deploy] Failed to parse node affinity JSON");
            }
        }

        if (hasPodAffinity)
        {
            try
            {
                using var doc = KubernetesPropertyParser.SafeParseJson(podAffinityRaw!);
                sb.AppendLine($"{innerIndent}podAffinity:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, $"{innerIndent}  ", doc.RootElement);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Deploy] Failed to parse pod affinity JSON");
            }
        }

        if (hasPodAntiAffinity)
        {
            try
            {
                using var doc = KubernetesPropertyParser.SafeParseJson(podAntiAffinityRaw!);
                sb.AppendLine($"{innerIndent}podAntiAffinity:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, $"{innerIndent}  ", doc.RootElement);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Deploy] Failed to parse pod anti-affinity JSON");
            }
        }
    }

    private static void AppendDnsConfigIfPresent(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        var optionsRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DnsConfigOptions).Trim();
        var nameserversRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodDnsNameservers);
        var searchesRaw = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.PodDnsSearches);

        var hasOptions = !string.IsNullOrWhiteSpace(optionsRaw)
            && !string.Equals(optionsRaw, KubernetesJsonLiterals.EmptyArray, StringComparison.Ordinal)
            && !string.Equals(optionsRaw, KubernetesJsonLiterals.EmptyObject, StringComparison.Ordinal);

        var nameservers = SplitCommaSeparated(nameserversRaw);
        var searches = SplitCommaSeparated(searchesRaw);

        if (!hasOptions && nameservers.Length == 0 && searches.Length == 0)
            return;

        var innerIndent = $"{indent}  ";

        sb.AppendLine($"{indent}dnsConfig:");

        if (nameservers.Length > 0)
        {
            sb.AppendLine($"{innerIndent}nameservers:");

            foreach (var ns in nameservers)
                sb.AppendLine($"{innerIndent}- {YamlSafeScalar.Escape(ns)}");
        }

        if (searches.Length > 0)
        {
            sb.AppendLine($"{innerIndent}searches:");

            foreach (var s in searches)
                sb.AppendLine($"{innerIndent}- {YamlSafeScalar.Escape(s)}");
        }

        if (hasOptions)
        {
            try
            {
                using var doc = KubernetesPropertyParser.SafeParseJson(optionsRaw);
                sb.AppendLine($"{innerIndent}options:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, $"{innerIndent}  ", doc.RootElement);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Deploy] Failed to parse DNS config options JSON");
            }
        }
    }

    private static void AppendPodSecurityContextIfPresent(StringBuilder sb, string indent, Dictionary<string, string> properties)
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
            && !string.Equals(sysctlsRaw, KubernetesJsonLiterals.EmptyArray, StringComparison.Ordinal)
            && !string.Equals(sysctlsRaw, KubernetesJsonLiterals.EmptyObject, StringComparison.Ordinal);

        var supplementalGroups = SplitCommaSeparated(supplementalGroupsRaw);
        var hasRunAsNonRoot = string.Equals(runAsNonRoot, KubernetesBooleanValues.True, StringComparison.OrdinalIgnoreCase);
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

        var innerIndent = $"{indent}  ";

        sb.AppendLine($"{indent}securityContext:");
        AppendIntValueIfPresent(sb, innerIndent, "fsGroup", fsGroup);
        AppendIntValueIfPresent(sb, innerIndent, "runAsUser", runAsUser);
        AppendIntValueIfPresent(sb, innerIndent, "runAsGroup", runAsGroup);

        if (hasRunAsNonRoot)
            sb.AppendLine($"{innerIndent}runAsNonRoot: true");

        if (supplementalGroups.Length > 0)
        {
            sb.AppendLine($"{innerIndent}supplementalGroups:");

            foreach (var g in supplementalGroups)
            {
                if (int.TryParse(g, out _))
                    sb.AppendLine($"{innerIndent}- {g}");
            }
        }

        if (hasSeLinux)
        {
            sb.AppendLine($"{innerIndent}seLinuxOptions:");

            var seIndent = $"{innerIndent}  ";

            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.Level, seLinuxLevel);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.Role, seLinuxRole);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.Type, seLinuxType);
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, seIndent, KubernetesSecurityContextPayloadProperties.User, seLinuxUser);
        }

        if (hasSysctls)
        {
            try
            {
                using var doc = KubernetesPropertyParser.SafeParseJson(sysctlsRaw);
                sb.AppendLine($"{innerIndent}sysctls:");
                KubernetesPropertyParser.AppendJsonElementYaml(sb, $"{innerIndent}  ", doc.RootElement);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Deploy] Failed to parse pod security sysctls JSON");
            }
        }
    }

    private static void AppendImagePullSecretsIfPresent(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue(KubernetesProperties.PodSecurityImagePullSecrets, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        raw = raw.Trim();

        if (string.Equals(raw, KubernetesJsonLiterals.EmptyArray, StringComparison.Ordinal) || string.Equals(raw, KubernetesJsonLiterals.EmptyObject, StringComparison.Ordinal))
            return;

        try
        {
            using var doc = KubernetesPropertyParser.SafeParseJson(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            sb.AppendLine($"{indent}imagePullSecrets:");

            foreach (var secret in doc.RootElement.EnumerateArray())
            {
                if (secret.TryGetProperty(KubernetesImagePullSecretPayloadProperties.Name, out var nameElement))
                    sb.AppendLine($"{indent}- name: {YamlSafeScalar.Escape(nameElement.GetString() ?? string.Empty)}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse image pull secrets JSON");
        }
    }

    private static void AppendHostAliasesIfPresent(StringBuilder sb, string indent, Dictionary<string, string> properties)
    {
        if (!properties.TryGetValue(KubernetesProperties.HostAliases, out var raw)
            || string.IsNullOrWhiteSpace(raw))
            return;

        raw = raw.Trim();

        if (string.Equals(raw, KubernetesJsonLiterals.EmptyArray, StringComparison.Ordinal) || string.Equals(raw, KubernetesJsonLiterals.EmptyObject, StringComparison.Ordinal))
            return;

        try
        {
            using var doc = KubernetesPropertyParser.SafeParseJson(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            var hasAny = false;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var ip = element.TryGetProperty(KubernetesHostAliasPayloadProperties.Ip, out var ipProp) ? ipProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(ip))
                    continue;

                if (!hasAny)
                {
                    sb.AppendLine($"{indent}hostAliases:");
                    hasAny = true;
                }

                sb.AppendLine($"{indent}- ip: {YamlSafeScalar.Escape(ip)}");

                var hostnames = ParseHostnames(element);

                if (hostnames.Length > 0)
                {
                    sb.AppendLine($"{indent}  hostnames:");

                    foreach (var hostname in hostnames)
                        sb.AppendLine($"{indent}  - {YamlSafeScalar.Escape(hostname)}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse host aliases JSON");
        }
    }

    private static string[] ParseHostnames(JsonElement element)
    {
        if (!element.TryGetProperty(KubernetesHostAliasPayloadProperties.Hostnames, out var hostnamesProp))
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

        return raw.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
