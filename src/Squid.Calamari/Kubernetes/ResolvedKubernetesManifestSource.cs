namespace Squid.Calamari.Kubernetes;

public sealed class ResolvedKubernetesManifestSource
{
    public required string ManifestRootDirectory { get; init; }

    public required IReadOnlyList<string> ManifestFilePaths { get; init; }

    public IReadOnlyList<string> CleanupPaths { get; init; } = [];

    public bool IsSingleFile => ManifestFilePaths.Count == 1;
}
