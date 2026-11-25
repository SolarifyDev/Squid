namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>The stored version has no facts: it was pushed before inspection existed, so no recipe can be chosen for it.</summary>
public sealed class PublishFactsMissingException(string packageId, string version) : InvalidOperationException($"Package '{packageId}' version '{version}' was pushed before packages were inspected, so no recipe can be chosen for it. Push it again to publish it.")
{
    public string PackageId { get; } = packageId;

    public string Version { get; } = version;
}
