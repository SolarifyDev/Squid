using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>What a push needs to know about the feed and the package it is pushed to before it writes anything.</summary>
internal interface IBuiltInPackageCatalog : IScopedDependency
{
    Task<ExternalFeed> GetFeedAsync(int spaceId, CancellationToken ct);

    /// <summary>The casing of the package id the feed keeps, fixing the given one as canonical when the feed has no such package yet.</summary>
    Task<string> ResolveCanonicalIdAsync(int feedId, string packageId, CancellationToken ct);

    /// <summary>The newest version of the package by version order, or null when it has none.</summary>
    Task<BuiltInPackage> GetLatestAsync(int feedId, string packageId, CancellationToken ct);
}

internal sealed class BuiltInPackageCatalog(IBuiltInFeedLocator feeds, IBuiltInPackageDataProvider packages) : IBuiltInPackageCatalog
{
    public Task<ExternalFeed> GetFeedAsync(int spaceId, CancellationToken ct) => feeds.GetAsync(spaceId, ct);

    public Task<string> ResolveCanonicalIdAsync(int feedId, string packageId, CancellationToken ct) => packages.ResolveCanonicalIdAsync(feedId, packageId, ct);

    public async Task<BuiltInPackage> GetLatestAsync(int feedId, string packageId, CancellationToken ct)
    {
        var existing = await packages.ListVersionsAsync(feedId, packageId, ct).ConfigureAwait(false);
        var latestVersion = PackageVersionFilter.SortByVersionDescending(existing.Select(v => v.Version)).FirstOrDefault();

        return existing.FirstOrDefault(v => v.Version == latestVersion);
    }
}
