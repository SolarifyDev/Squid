using k8s.Models;

namespace Squid.Tentacle.Kubernetes;

public partial class KubernetesPodManager
{
    private V1Pod BuildPodSpec(string podName, string ticketId)
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                NamespaceProperty = _settings.TentacleNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "squid-tentacle",
                    ["squid.io/ticket-id"] = ticketId
                }
            },
            Spec = new V1PodSpec
            {
                ServiceAccountName = _settings.ScriptPodServiceAccount,
                RestartPolicy = "Never",
                ActiveDeadlineSeconds = _settings.ScriptPodTimeoutSeconds,
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = "script",
                        Image = _settings.ScriptPodImage,
                        Command = new[] { "squid-calamari" },
                        Args = new[] { "run-script", $"--script=/squid/work/{ticketId}/script.sh", $"--variables=/squid/work/{ticketId}/variables.json" },
                        WorkingDir = $"/squid/work/{ticketId}",
                        VolumeMounts = new List<V1VolumeMount>
                        {
                            new() { Name = "workspace", MountPath = "/squid/work" }
                        },
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
                Volumes = new List<V1Volume>
                {
                    new()
                    {
                        Name = "workspace",
                        PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                        {
                            ClaimName = _settings.PvcClaimName
                        }
                    }
                }
            }
        };
    }
}
