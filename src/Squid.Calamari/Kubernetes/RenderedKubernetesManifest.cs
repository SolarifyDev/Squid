namespace Squid.Calamari.Kubernetes;

public sealed class RenderedKubernetesManifest
{
    public required string ApplyPath { get; init; }

    public bool Recursive { get; init; }
}
