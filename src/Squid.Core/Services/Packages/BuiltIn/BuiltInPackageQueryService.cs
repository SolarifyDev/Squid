using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Models.BuiltInPackages;
using Squid.Message.Requests.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Reads of a space's built-in feed. A space is addressed by id and the feed is found from it, so a caller cannot reach another space's feed by guessing a feed id.</summary>
public interface IBuiltInPackageQueryService : IScopedDependency
{
    Task<ListBuiltInPackagesResponseData> ListPackagesAsync(ListBuiltInPackagesRequest request, CancellationToken ct);

    Task<ListBuiltInPackageVersionsResponseData> ListVersionsAsync(ListBuiltInPackageVersionsRequest request, CancellationToken ct);

    Task<BuiltInPackageVersionDto> GetVersionAsync(GetBuiltInPackageVersionRequest request, CancellationToken ct);
}

public sealed class BuiltInPackageQueryService(IBuiltInFeedLocator feeds, IBuiltInPackageDataProvider packages) : IBuiltInPackageQueryService
{
    public async Task<ListBuiltInPackagesResponseData> ListPackagesAsync(ListBuiltInPackagesRequest request, CancellationToken ct)
    {
        var feed = await feeds.GetAsync(request.SpaceId.GetValueOrDefault(), ct).ConfigureAwait(false);
        var found = await packages.ListPackagesAsync(feed.Id, request.Filter, request.Take, ct).ConfigureAwait(false);

        return new ListBuiltInPackagesResponseData { FeedId = feed.Id, Packages = found };
    }

    public async Task<ListBuiltInPackageVersionsResponseData> ListVersionsAsync(ListBuiltInPackageVersionsRequest request, CancellationToken ct)
    {
        var feed = await feeds.GetAsync(request.SpaceId.GetValueOrDefault(), ct).ConfigureAwait(false);
        var versions = await packages.ListVersionsAsync(feed.Id, request.PackageId ?? string.Empty, ct).ConfigureAwait(false);

        if (versions.Count == 0)
            throw new BuiltInPackageNotFoundException(feed.Id, request.PackageId, null);

        var newestFirst = PackageVersionFilter.SortByVersionDescending(versions.Select(v => v.Version));

        return new ListBuiltInPackageVersionsResponseData { Versions = newestFirst.Select(v => BuiltInPackageMapper.ToDto(versions.First(p => p.Version == v))).ToList() };
    }

    public async Task<BuiltInPackageVersionDto> GetVersionAsync(GetBuiltInPackageVersionRequest request, CancellationToken ct)
    {
        var feed = await feeds.GetAsync(request.SpaceId.GetValueOrDefault(), ct).ConfigureAwait(false);
        var package = await packages.GetAsync(feed.Id, request.PackageId ?? string.Empty, request.Version ?? string.Empty, ct).ConfigureAwait(false);

        return package == null ? throw new BuiltInPackageNotFoundException(feed.Id, request.PackageId, request.Version) : BuiltInPackageMapper.ToDto(package, withFacts: true);
    }
}
