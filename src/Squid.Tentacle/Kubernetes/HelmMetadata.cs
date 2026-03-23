using k8s.Models;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Kubernetes;

public static class HelmMetadata
{
    public static void ApplyHelmAnnotations(V1ObjectMeta metadata, KubernetesSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ReleaseName)) return;

        metadata.Annotations ??= new Dictionary<string, string>();
        metadata.Annotations["meta.helm.sh/release-name"] = settings.ReleaseName;
        metadata.Annotations["meta.helm.sh/release-namespace"] = settings.TentacleNamespace;
    }
}
