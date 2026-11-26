using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Constants;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

/// <summary>A built-in package carries no release notes, only the date it was pushed. The feed type is matched exactly.</summary>
public class BuiltInPackageNotesStrategy(IBuiltInPackageDataProvider packages) : IPackageNotesStrategy
{
    public bool CanHandle(string feedType) => BuiltInFeed.IsBuiltIn(feedType);

    public async Task<PackageNotesResult> GetNotesAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version)) return PackageNotesResult.Empty();

        var package = await packages.GetAsync(feed.Id, packageId, version, ct).ConfigureAwait(false);

        return package == null ? PackageNotesResult.Failure($"Version {version} of package '{packageId}' is not in the built-in feed") : PackageNotesResult.Success(null, package.PublishedDate);
    }
}
