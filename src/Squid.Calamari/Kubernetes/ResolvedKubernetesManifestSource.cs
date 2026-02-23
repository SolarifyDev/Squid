namespace Squid.Calamari.Kubernetes;

public sealed class ResolvedKubernetesManifestSource
{
    public required string ManifestFilePath { get; init; }

    public IReadOnlyList<string> CleanupPaths { get; init; } = [];
}
