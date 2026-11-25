using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.GitHub;

public class GitHubFeedPackageSource(FeedPackageDownloader downloader) : IFeedPackageSource
{
    public bool CanHandle(string feedType) => PackageFeedKinds.Classify(feedType) == PackageFeedKind.GitHub;

    public Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct) =>
        downloader.FetchAsync(feed, packageId, version, () => Task.FromResult(BuildDownloadUrl(feed, packageId, version)), ct);

    internal static string BuildDownloadUrl(ExternalFeed feed, string packageId, string version) =>
        $"{(feed.FeedUri ?? string.Empty).TrimEnd('/')}/repos/{packageId}/tarball/{version}";
}
