namespace Squid.Calamari.Kubernetes;

public interface IKubernetesManifestRenderer
{
    Task<string> RenderToFileAsync(KubernetesApplyRequest request, CancellationToken ct);
}
