namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class PackageVersionInUseException(string packageId, string version) : InvalidOperationException($"Version {version} of package '{packageId}' is selected by a release and cannot be replaced; push a new version instead")
{
    public string PackageId { get; } = packageId;

    public string Version { get; } = version;
}
