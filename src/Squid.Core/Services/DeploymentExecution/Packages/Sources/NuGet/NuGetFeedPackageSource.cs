using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.NuGet;

public class NuGetFeedPackageSource(FeedPackageDownloader downloader) : IFeedPackageSource
{
    public bool CanHandle(string feedType) => PackageFeedKinds.Classify(feedType) == PackageFeedKind.NuGet;

    public Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct) =>
        downloader.FetchAsync(feed, packageId, version, () => Task.FromResult(BuildDownloadUrl(feed, packageId, version)), ct);

    internal static string BuildDownloadUrl(ExternalFeed feed, string packageId, string version)
    {
        // Package content still uses the NuGet V2 download path. Operators often
        // paste a V3 service-index URI (…/v3 or …/v3/index.json). Strip that suffix
        // so we never construct …/v3/api/v2/package/... (404 / empty content).
        // nuget.org additionally needs the www host for the public V2 package path.
        var nugetBase = NormalizeNuGetV2DownloadBase((feed.FeedUri ?? string.Empty).TrimEnd('/'));

        return $"{nugetBase}/api/v2/package/{packageId}/{version}";
    }

    /// <summary>
    /// Maps a NuGet feed URI to the base used for V2 package downloads.
    /// </summary>
    internal static string NormalizeNuGetV2DownloadBase(string feedUri)
    {
        var baseUri = (feedUri ?? string.Empty).Trim();
        if (baseUri.Length == 0)
            return string.Empty;

        baseUri = baseUri.TrimEnd('/');

        // Common operator paste forms of the V3 service index.
        if (baseUri.EndsWith("/v3/index.json", StringComparison.OrdinalIgnoreCase))
            baseUri = baseUri[..^"/v3/index.json".Length];
        else if (baseUri.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase)
                 && baseUri.Contains("/v3", StringComparison.OrdinalIgnoreCase))
            baseUri = baseUri[..^"/index.json".Length].TrimEnd('/');

        if (baseUri.EndsWith("/v3", StringComparison.OrdinalIgnoreCase))
            baseUri = baseUri[..^3].TrimEnd('/');

        // Public nuget.org: V2 package downloads are served from www.nuget.org, not api.nuget.org/v3.
        if (Uri.TryCreate(baseUri, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("nuget.org", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.nuget.org", StringComparison.OrdinalIgnoreCase)))
        {
            return "https://www.nuget.org";
        }

        return baseUri.TrimEnd('/');
    }
}
