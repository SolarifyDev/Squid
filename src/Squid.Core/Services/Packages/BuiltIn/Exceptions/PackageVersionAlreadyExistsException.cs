namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class PackageVersionAlreadyExistsException(string packageId, string version) : InvalidOperationException($"Version {version} of package '{packageId}' already exists in the built-in feed; push it again with replace=true to overwrite it, or choose another version")
{
    public string PackageId { get; } = packageId;

    public string Version { get; } = version;
}
