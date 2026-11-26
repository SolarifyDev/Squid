using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Generic;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.GitHub;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Helm;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.NuGet;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.Execution.Packages;

/// <summary>Hand-wires the production fetcher graph for tests that do not use the container.</summary>
internal static class PackageFetcherWiring
{
    public static IPackageContentFetcher Create(ISquidHttpClientFactory factory)
    {
        var downloader = new FeedPackageDownloader(factory);

        return new RoutingPackageContentFetcher(Sources(factory, downloader), new GenericFeedPackageSource(downloader));
    }

    public static IFeedPackageSource[] Sources(ISquidHttpClientFactory factory, FeedPackageDownloader downloader) => new IFeedPackageSource[]
    {
        new HelmFeedPackageSource(factory, downloader),
        new GitHubFeedPackageSource(downloader),
        new NuGetFeedPackageSource(downloader)
    };
}
