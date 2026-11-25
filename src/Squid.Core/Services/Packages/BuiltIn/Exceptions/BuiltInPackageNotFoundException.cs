namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class BuiltInPackageNotFoundException(int feedId, string packageId, string version) : InvalidOperationException(Describe(feedId, packageId, version))
{
    public int FeedId { get; } = feedId;

    public string PackageId { get; } = packageId;

    /// <summary>Null when the whole package, not one version, was not found.</summary>
    public string Version { get; } = version;

    private static string Describe(int feedId, string packageId, string version)
    {
        return version == null ? $"The built-in feed {feedId} has no package '{packageId}'" : $"The built-in feed {feedId} has no version '{version}' of package '{packageId}'";
    }
}
