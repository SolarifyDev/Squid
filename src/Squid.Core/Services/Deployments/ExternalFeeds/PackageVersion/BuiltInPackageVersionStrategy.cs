using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Constants;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

/// <summary>Lists the versions of a built-in feed package from the index. The feed type is matched exactly, so no substring-matched strategy of another feed type can claim it.</summary>
public class BuiltInPackageVersionStrategy(IBuiltInPackageDataProvider packages) : IPackageVersionStrategy
{
    public bool CanHandle(string feedType) => BuiltInFeed.IsBuiltIn(feedType);

    public async Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return [];

        var versions = await packages.ListVersionsAsync(feed.Id, packageId, ct).ConfigureAwait(false);

        return versions.Select(v => v.Version).Take(PackageVersionEnumerationCap.Resolve()).ToList();
    }
}
