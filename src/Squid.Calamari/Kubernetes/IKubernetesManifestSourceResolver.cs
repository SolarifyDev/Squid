namespace Squid.Calamari.Kubernetes;

public interface IKubernetesManifestSourceResolver
{
    Task<ResolvedKubernetesManifestSource> ResolveAsync(string manifestSourcePath, CancellationToken ct);
}
