using System.Text.Json;
using k8s.Models;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public partial class KubernetesPodManager
{
    private V1Pod BuildPodSpec(string podName, string ticketId)
    {
        var useInitContainer = !string.IsNullOrEmpty(_settings.TentacleImage);

        var volumes = new List<V1Volume>
        {
            new()
            {
                Name = "workspace",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName = _settings.PvcClaimName
                }
            }
        };

        var mainVolumeMounts = new List<V1VolumeMount>
        {
            new() { Name = "workspace", MountPath = "/squid/work" }
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
                NamespaceProperty = _settings.TentacleNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "kubernetes-agent",
                    ["squid.io/ticket-id"] = ticketId
                }
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
                        Command = new[] { "/squid/bin/squid-calamari" },
                        Args = new[] { "run-script", $"--script=/squid/work/{ticketId}/script.sh", $"--variables=/squid/work/{ticketId}/variables.json" },
                        WorkingDir = $"/squid/work/{ticketId}",
                        VolumeMounts = mainVolumeMounts,
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
                    Command = new[] { "cp", "/squid/bin/squid-calamari", "/squid-bin/squid-calamari" },
                    VolumeMounts = new List<V1VolumeMount>
                    {
                        new() { Name = "squid-bin", MountPath = "/squid-bin" }
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

        return pod;
    }

    private V1PodSecurityContext BuildPodSecurityContext()
    {
        if (_settings.ScriptPodRunAsUser == null && !_settings.ScriptPodRunAsNonRoot)
            return null;

        var ctx = new V1PodSecurityContext();

        if (_settings.ScriptPodRunAsUser.HasValue)
            ctx.RunAsUser = _settings.ScriptPodRunAsUser.Value;

        if (_settings.ScriptPodRunAsNonRoot)
            ctx.RunAsNonRoot = true;

        return ctx;
    }

    private List<V1LocalObjectReference> BuildImagePullSecrets()
    {
        if (string.IsNullOrWhiteSpace(_settings.ScriptPodImagePullSecrets)) return null;

        return _settings.ScriptPodImagePullSecrets
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => new V1LocalObjectReference(name))
            .ToList();
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
}
