using k8s.Models;

namespace Squid.Tentacle.Kubernetes;

/// <summary>
/// CRD model for squid.io/v1alpha1/ScriptPodTemplate.
/// Cluster admins can create this resource to override default pod spec values.
/// </summary>
public class ScriptPodTemplate
{
    public string Image { get; set; }

    public V1ResourceRequirements Resources { get; set; }

    public List<V1Toleration> Tolerations { get; set; }

    public Dictionary<string, string> NodeSelector { get; set; }

    public V1Affinity Affinity { get; set; }

    public List<V1Volume> AdditionalVolumes { get; set; }

    public List<V1VolumeMount> AdditionalVolumeMounts { get; set; }

    public List<V1EnvVar> AdditionalEnvVars { get; set; }
}
