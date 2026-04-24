using System.Text.Json;
using k8s.Models;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public partial class KubernetesPodManager
{
    private V1Pod BuildPodSpec(string podName, string ticketId, string ns, Dictionary<string, string>? additionalLabels = null)
    {
        // P0-C.1 (refactored under Phase-3 three-mode hardening pattern): validate
        // ScriptPodImage against the digest-pin requirement. Default mode is Warn —
        // tag-only references log a structured warning but proceed (preserves
        // backward compat for deploys shipped with the original tag-based default).
        // Operators flip SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT=strict for production
        // hardening; CLAUDE.md §"Hardening Three-Mode Enforcement" for the pattern.
        ScriptPodImageValidator.EnsureSafe(_settings.ScriptPodImage, ScriptPodImageValidator.ReadMode());

        var template = _templateProvider?.TryLoadTemplate();
        var rawScriptMode = _settings.RawScriptMode || string.IsNullOrEmpty(_settings.TentacleImage);
        var useInitContainer = !rawScriptMode;
        var isolateWorkspace = _settings.IsolateWorkspaceToEmptyDir;

        var workspaceVolumeName = isolateWorkspace ? "workspace-local" : "workspace";

        var volumes = new List<V1Volume>
        {
            new()
            {
                Name = isolateWorkspace ? "workspace-nfs" : "workspace",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName = _settings.PvcClaimName
                }
            }
        };

        if (isolateWorkspace)
            volumes.Add(new V1Volume { Name = "workspace-local", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("1Gi") } });

        var mainVolumeMounts = new List<V1VolumeMount>
        {
            new() { Name = workspaceVolumeName, MountPath = "/squid/work" }
        };

        if (useInitContainer)
        {
            volumes.Add(new V1Volume { Name = "squid-bin", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity("256Mi") } });
            mainVolumeMounts.Add(new V1VolumeMount { Name = "squid-bin", MountPath = "/squid/bin" });
        }

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = ns,
                Labels = BuildLabels(ticketId, additionalLabels),
                Annotations = BuildAnnotations()
            },
            Spec = new V1PodSpec
            {
                ServiceAccountName = _settings.ScriptPodServiceAccount,
                RestartPolicy = "Never",
                ActiveDeadlineSeconds = _settings.ScriptPodTimeoutSeconds,
                SecurityContext = BuildPodSecurityContext(),
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = "script",
                        Image = _settings.ScriptPodImage,
                        ImagePullPolicy = "IfNotPresent",
                        Command = rawScriptMode ? new[] { "bash" } : new[] { "/squid/bin/squid-calamari" },
                        Args = rawScriptMode
                            ? new[] { $"/squid/work/{ticketId}/script.sh" }
                            : new[] { "run-script", $"--script=/squid/work/{ticketId}/script.sh", $"--variables=/squid/work/{ticketId}/variables.json" },
                        WorkingDir = $"/squid/work/{ticketId}",
                        VolumeMounts = mainVolumeMounts,
                        Env = MergeEnvVars(BuildStandardEnvVars(ticketId), BuildProxyEnvVars()),
                        SecurityContext = BuildContainerSecurityContext(),
                        Resources = new V1ResourceRequirements
                        {
                            Requests = new Dictionary<string, ResourceQuantity>
                            {
                                ["cpu"] = new(_settings.ScriptPodCpuRequest),
                                ["memory"] = new(_settings.ScriptPodMemoryRequest)
                            },
                            Limits = new Dictionary<string, ResourceQuantity>
                            {
                                ["cpu"] = new(_settings.ScriptPodCpuLimit),
                                ["memory"] = new(_settings.ScriptPodMemoryLimit)
                            }
                        }
                    }
                },
                ImagePullSecrets = BuildImagePullSecrets(),
                Tolerations = BuildTolerations(),
                Volumes = volumes
            }
        };

        if (useInitContainer)
        {
            pod.Spec.InitContainers = new List<V1Container>
            {
                new()
                {
                    Name = "copy-calamari",
                    Image = _settings.TentacleImage,
                    Command = new[] { "sh", "-c", "cp -a /squid/bin/. /squid-bin/" },
                    VolumeMounts = new List<V1VolumeMount>
                    {
                        new() { Name = "squid-bin", MountPath = "/squid-bin" }
                    },
                    // Tentacle image may run as root — override pod-level runAsNonRoot
                    // since this init container only copies a binary
                    SecurityContext = new V1SecurityContext
                    {
                        RunAsNonRoot = false,
                        AllowPrivilegeEscalation = false,
                        Capabilities = new V1Capabilities { Drop = new List<string> { "ALL" } }
                    },
                    Resources = new V1ResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity>
                        {
                            ["cpu"] = new("10m"),
                            ["memory"] = new("50Mi")
                        },
                        Limits = new Dictionary<string, ResourceQuantity>
                        {
                            ["cpu"] = new("100m"),
                            ["memory"] = new("128Mi")
                        }
                    }
                }
            };
        }

        if (isolateWorkspace)
        {
            pod.Spec.InitContainers ??= new List<V1Container>();
            pod.Spec.InitContainers.Add(BuildCopyWorkspaceInitContainer(ticketId));
        }

        if (!string.IsNullOrEmpty(_settings.NfsWatchdogImage))
            pod.Spec.Containers.Add(BuildNfsWatchdogSidecar(isolateWorkspace));

        var nodeSelector = BuildNodeSelector();
        if (nodeSelector != null)
            pod.Spec.NodeSelector = nodeSelector;

        var rwoAffinity = BuildRwoAffinity();
        if (rwoAffinity != null)
            pod.Spec.Affinity = rwoAffinity;

        HelmMetadata.ApplyHelmAnnotations(pod.Metadata, _settings);
        ApplyTemplateOverrides(pod, template);

        return pod;
    }

    private V1Container BuildNfsWatchdogSidecar(bool isolateWorkspace)
    {
        var volumeName = isolateWorkspace ? "workspace-nfs" : "workspace";

        return new V1Container
        {
            Name = "nfs-watchdog",
            Image = _settings.NfsWatchdogImage,
            ImagePullPolicy = "IfNotPresent",
            Env = new List<V1EnvVar> { new() { Name = "WATCHDOG_DIRECTORY", Value = "/squid/work" } },
            VolumeMounts = new List<V1VolumeMount> { new() { Name = volumeName, MountPath = "/squid/work", ReadOnlyProperty = true } },
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("32Mi") },
                Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("50m"), ["memory"] = new("64Mi") }
            }
        };
    }

    private V1Container BuildCopyWorkspaceInitContainer(string ticketId)
    {
        return new V1Container
        {
            Name = "copy-workspace",
            Image = _settings.ScriptPodImage,
            Command = new[] { "sh", "-c", $"cp -a /squid/nfs-work/{ticketId}/. /squid/work/{ticketId}/" },
            VolumeMounts = new List<V1VolumeMount>
            {
                new() { Name = "workspace-nfs", MountPath = "/squid/nfs-work", ReadOnlyProperty = true },
                new() { Name = "workspace-local", MountPath = "/squid/work" }
            },
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("50Mi") },
                Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("128Mi") }
            }
        };
    }

    private V1Affinity BuildRwoAffinity()
    {
        if (!string.Equals(_settings.PersistenceAccessMode, "ReadWriteOnce", StringComparison.OrdinalIgnoreCase))
            return null;

        return new V1Affinity
        {
            PodAffinity = new V1PodAffinity
            {
                RequiredDuringSchedulingIgnoredDuringExecution = new List<V1PodAffinityTerm>
                {
                    new()
                    {
                        LabelSelector = new V1LabelSelector
                        {
                            MatchLabels = new Dictionary<string, string>
                            {
                                ["app.kubernetes.io/name"] = "kubernetes-agent",
                                ["app.kubernetes.io/instance"] = _settings.ReleaseName
                            }
                        },
                        TopologyKey = "kubernetes.io/hostname"
                    }
                }
            }
        };
    }

    private static void ApplyTemplateOverrides(V1Pod pod, ScriptPodTemplate template)
    {
        if (template == null) return;

        var mainContainer = pod.Spec.Containers[0];

        if (!string.IsNullOrEmpty(template.Image))
        {
            // P0-C.1 defence-in-depth: template.Image override must also pass the
            // digest-pin check under the same enforcement mode. Otherwise an operator
            // could comply at _settings.ScriptPodImage and sidestep via a template
            // override.
            ScriptPodImageValidator.EnsureSafe(template.Image, ScriptPodImageValidator.ReadMode());
            mainContainer.Image = template.Image;
        }

        if (template.Resources != null)
            mainContainer.Resources = template.Resources;

        if (template.Tolerations is { Count: > 0 })
            pod.Spec.Tolerations = template.Tolerations;

        if (template.NodeSelector is { Count: > 0 })
            pod.Spec.NodeSelector = template.NodeSelector;

        if (template.Affinity != null)
            pod.Spec.Affinity = template.Affinity;

        if (template.AdditionalVolumes is { Count: > 0 })
        {
            pod.Spec.Volumes ??= new List<V1Volume>();
            pod.Spec.Volumes = pod.Spec.Volumes.Concat(template.AdditionalVolumes).ToList();
        }

        if (template.AdditionalVolumeMounts is { Count: > 0 })
        {
            mainContainer.VolumeMounts ??= new List<V1VolumeMount>();
            mainContainer.VolumeMounts = mainContainer.VolumeMounts.Concat(template.AdditionalVolumeMounts).ToList();
        }

        if (template.AdditionalEnvVars is { Count: > 0 })
        {
            mainContainer.Env ??= new List<V1EnvVar>();
            mainContainer.Env = mainContainer.Env.Concat(template.AdditionalEnvVars).ToList();
        }
    }

    private Dictionary<string, string> BuildLabels(string ticketId, Dictionary<string, string>? additionalLabels = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "kubernetes-agent",
            ["squid.io/ticket-id"] = ticketId
        };

        if (!string.IsNullOrEmpty(_settings.ReleaseName))
            labels["app.kubernetes.io/instance"] = _settings.ReleaseName;

        MergeCustomLabels(labels);
        MergeCommandLabels(labels, additionalLabels);

        return labels;
    }

    private static void MergeCommandLabels(Dictionary<string, string> labels, Dictionary<string, string>? commandLabels)
    {
        if (commandLabels == null) return;

        foreach (var kvp in commandLabels)
        {
            if (!ValidateLabelKey(kvp.Key))
            {
                Log.Warning("Rejected reserved command label key {Key}", kvp.Key);
                continue;
            }

            labels[kvp.Key] = kvp.Value;
        }
    }

    private void MergeCustomLabels(Dictionary<string, string> labels)
    {
        if (string.IsNullOrWhiteSpace(_settings.ScriptPodLabels)) return;

        try
        {
            var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(_settings.ScriptPodLabels);
            if (custom == null) return;

            foreach (var kvp in custom)
            {
                if (!ValidateLabelKey(kvp.Key))
                {
                    Log.Warning("Rejected reserved label key {Key}", kvp.Key);
                    continue;
                }

                labels[kvp.Key] = kvp.Value;
            }
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse ScriptPodLabels JSON");
        }
    }

    private Dictionary<string, string> BuildAnnotations()
    {
        if (string.IsNullOrWhiteSpace(_settings.ScriptPodAnnotations)) return null;

        try
        {
            var annotations = JsonSerializer.Deserialize<Dictionary<string, string>>(_settings.ScriptPodAnnotations);

            return annotations is { Count: > 0 } ? annotations : null;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse ScriptPodAnnotations JSON");
            return null;
        }
    }

    internal static bool ValidateLabelKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        var prefix = key.Contains('/') ? key[..key.IndexOf('/')] : "";

        return !prefix.EndsWith("kubernetes.io", StringComparison.OrdinalIgnoreCase)
            && !prefix.EndsWith("k8s.io", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> BuildNodeSelector()
    {
        Dictionary<string, string> selector = null;

        if (!string.IsNullOrWhiteSpace(_settings.ScriptPodNodeArchitecture))
        {
            selector = new Dictionary<string, string>
            {
                ["kubernetes.io/arch"] = _settings.ScriptPodNodeArchitecture
            };
        }

        if (!string.IsNullOrWhiteSpace(_settings.ScriptPodNodeSelector))
        {
            try
            {
                var explicit_ = JsonSerializer.Deserialize<Dictionary<string, string>>(_settings.ScriptPodNodeSelector);

                if (explicit_ is { Count: > 0 })
                {
                    selector ??= new Dictionary<string, string>();

                    foreach (var kvp in explicit_)
                        selector[kvp.Key] = kvp.Value;
                }
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Failed to parse ScriptPodNodeSelector JSON");
            }
        }

        return selector;
    }

    private V1PodSecurityContext BuildPodSecurityContext()
    {
        var ctx = new V1PodSecurityContext
        {
            RunAsNonRoot = _settings.ScriptPodRunAsNonRoot || !_settings.ScriptPodRunAsUser.HasValue
        };

        if (_settings.ScriptPodRunAsUser.HasValue)
            ctx.RunAsUser = _settings.ScriptPodRunAsUser.Value;

        return ctx;
    }

    private static V1SecurityContext BuildContainerSecurityContext()
    {
        return new V1SecurityContext
        {
            AllowPrivilegeEscalation = false,
            Capabilities = new V1Capabilities
            {
                Drop = new List<string> { "ALL" }
            }
        };
    }

    private List<V1LocalObjectReference> BuildImagePullSecrets()
    {
        var secrets = new List<V1LocalObjectReference>();

        if (!string.IsNullOrWhiteSpace(_settings.ScriptPodImagePullSecrets))
        {
            secrets.AddRange(_settings.ScriptPodImagePullSecrets
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new V1LocalObjectReference(name)));
        }

        var dynamicSecrets = _pullSecretManager?.EnsureAllPullSecrets();

        if (dynamicSecrets != null)
            secrets.AddRange(dynamicSecrets.Select(name => new V1LocalObjectReference(name)));

        return secrets.Count > 0 ? secrets : null;
    }

    private List<V1Toleration> BuildTolerations()
    {
        if (string.IsNullOrWhiteSpace(_settings.ScriptPodTolerations)) return null;

        try
        {
            return JsonSerializer.Deserialize<List<V1Toleration>>(_settings.ScriptPodTolerations);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse ScriptPodTolerations JSON");
            return null;
        }
    }

    private List<V1EnvVar> BuildStandardEnvVars(string ticketId)
    {
        return new List<V1EnvVar>
        {
            new() { Name = "SQUID_RUNNING_IN_CONTAINER", Value = "true" },
            new() { Name = "SQUID_TICKET_ID", Value = ticketId },
            new() { Name = "SQUID_NAMESPACE", Value = _settings.TentacleNamespace },
            new() { Name = "SQUID_WORKSPACE", Value = $"/squid/work/{ticketId}" }
        };
    }

    private static List<V1EnvVar> MergeEnvVars(params List<V1EnvVar>?[] sources)
    {
        var merged = new List<V1EnvVar>();

        foreach (var source in sources)
            if (source != null) merged.AddRange(source);

        return merged.Count > 0 ? merged : null;
    }

    private List<V1EnvVar> BuildProxyEnvVars()
    {
        if (!string.IsNullOrEmpty(_settings.ProxySecretName))
            return BuildProxyEnvVarsFromSecret();

        return BuildProxyEnvVarsInline();
    }

    private List<V1EnvVar> BuildProxyEnvVarsFromSecret()
    {
        var envVars = new List<V1EnvVar>();

        void AddFromSecret(string envName, string key)
        {
            envVars.Add(new V1EnvVar
            {
                Name = envName,
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = _settings.ProxySecretName,
                        Key = key,
                        Optional = true
                    }
                }
            });
        }

        AddFromSecret("http_proxy", "http-proxy");
        AddFromSecret("HTTP_PROXY", "http-proxy");
        AddFromSecret("https_proxy", "https-proxy");
        AddFromSecret("HTTPS_PROXY", "https-proxy");
        AddFromSecret("no_proxy", "no-proxy");
        AddFromSecret("NO_PROXY", "no-proxy");

        return envVars;
    }

    private List<V1EnvVar> BuildProxyEnvVarsInline()
    {
        var envVars = new List<V1EnvVar>();

        if (!string.IsNullOrEmpty(_settings.HttpProxy))
        {
            envVars.Add(new V1EnvVar { Name = "http_proxy", Value = _settings.HttpProxy });
            envVars.Add(new V1EnvVar { Name = "HTTP_PROXY", Value = _settings.HttpProxy });
        }

        if (!string.IsNullOrEmpty(_settings.HttpsProxy))
        {
            envVars.Add(new V1EnvVar { Name = "https_proxy", Value = _settings.HttpsProxy });
            envVars.Add(new V1EnvVar { Name = "HTTPS_PROXY", Value = _settings.HttpsProxy });
        }

        if (!string.IsNullOrEmpty(_settings.NoProxy))
        {
            envVars.Add(new V1EnvVar { Name = "no_proxy", Value = _settings.NoProxy });
            envVars.Add(new V1EnvVar { Name = "NO_PROXY", Value = _settings.NoProxy });
        }

        return envVars.Count > 0 ? envVars : null;
    }
}
