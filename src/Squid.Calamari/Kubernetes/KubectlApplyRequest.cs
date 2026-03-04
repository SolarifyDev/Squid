namespace Squid.Calamari.Kubernetes;

public sealed class KubectlApplyRequest
{
    public required string WorkingDirectory { get; init; }

    public required string ManifestFilePath { get; init; }

    public string? Namespace { get; init; }

    public bool Recursive { get; init; }
}
