namespace Squid.Calamari.Compatibility.Octopus;

public sealed class ResolvedRawYamlPackage
{
    public required string YamlFilePath { get; init; }

    public IReadOnlyList<string> CleanupPaths { get; init; } = [];
}
