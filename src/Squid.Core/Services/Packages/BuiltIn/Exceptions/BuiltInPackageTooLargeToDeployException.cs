namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

/// <summary>A deployment holds a package in one byte array, so a stored package bigger than an array can be is kept and listed but cannot be deployed.</summary>
public sealed class BuiltInPackageTooLargeToDeployException(string packageId, string version, long sizeBytes) : InvalidOperationException($"Package '{packageId}' version {version} is {sizeBytes} bytes, more than a deployment can load into memory ({Array.MaxLength} bytes)")
{
    public string PackageId { get; } = packageId;

    public string Version { get; } = version;
}
