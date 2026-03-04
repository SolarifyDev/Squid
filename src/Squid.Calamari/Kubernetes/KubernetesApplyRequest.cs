using Squid.Calamari.Variables;

namespace Squid.Calamari.Kubernetes;

public sealed class KubernetesApplyRequest
{
    public required string WorkingDirectory { get; init; }

    public required string YamlFilePath { get; init; }

    public required VariableSet Variables { get; init; }

    public string? Namespace { get; init; }

    public ICollection<string>? TemporaryFiles { get; init; }
}
