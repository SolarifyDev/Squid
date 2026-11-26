namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

/// <summary>The stored file no longer matches the SHA-256 recorded when it was pushed; it is not handed to a deployment.</summary>
public sealed class BuiltInPackageIntegrityException(string packageId, string version, string expected, string actual) : InvalidOperationException($"The stored file of package '{packageId}' version {version} does not match its recorded SHA-256 (expected {expected}, found {actual}); the package store has lost or altered it")
{
    public string PackageId { get; } = packageId;

    public string Version { get; } = version;
}
