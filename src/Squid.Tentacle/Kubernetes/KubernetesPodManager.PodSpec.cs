using k8s.Models;

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
            volumes.Add(new V1Volume { Name = "squid-bin", EmptyDir = new V1EmptyDirVolumeSource() });
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
                SecurityContext = new V1PodSecurityContext { RunAsUser = 0 },
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
                    }
                }
            };
        }

        return pod;
    }
}
