namespace Squid.Calamari.Compatibility.Octopus;

public interface IOctopusRawYamlPackageResolver
{
    Task<ResolvedRawYamlPackage> ResolveAsync(string packagePath, CancellationToken ct);
}
