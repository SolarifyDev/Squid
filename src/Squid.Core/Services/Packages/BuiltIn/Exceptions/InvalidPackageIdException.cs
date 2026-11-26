namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class InvalidPackageIdException(string packageId, string reason) : InvalidOperationException($"'{packageId}' is not a valid package id: {reason}")
{
    public string PackageId { get; } = packageId;
}
