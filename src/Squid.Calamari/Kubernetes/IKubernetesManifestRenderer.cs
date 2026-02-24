namespace Squid.Calamari.Kubernetes;

public interface IKubernetesManifestRenderer
{
    Task<RenderedKubernetesManifest> RenderAsync(
        KubernetesApplyRequest request,
        ResolvedKubernetesManifestSource source,
        CancellationToken ct);
}
