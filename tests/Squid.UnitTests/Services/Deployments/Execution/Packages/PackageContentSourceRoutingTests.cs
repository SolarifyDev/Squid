using System.Linq;
using System.Reflection;
using Autofac;
using Microsoft.Extensions.Configuration;
using Squid.Core;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Generic;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.GitHub;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Helm;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.NuGet;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.Execution.Packages;

public class PackageContentSourceRoutingTests
{
    private static readonly ISquidHttpClientFactory Http = new Mock<ISquidHttpClientFactory>().Object;

    private static IFeedPackageSource[] AllSources() => PackageFetcherWiring.Sources(Http, new FeedPackageDownloader(Http));

    [Theory]
    [InlineData("Helm", typeof(HelmFeedPackageSource))]
    [InlineData("helm feed", typeof(HelmFeedPackageSource))]
    [InlineData("GitHub", typeof(GitHubFeedPackageSource))]
    [InlineData("GITHUB Repository Feed", typeof(GitHubFeedPackageSource))]
    [InlineData("NuGet", typeof(NuGetFeedPackageSource))]
    [InlineData("nuget feed", typeof(NuGetFeedPackageSource))]
    [InlineData("GitHub Helm", typeof(HelmFeedPackageSource))]
    [InlineData("NuGet GitHub", typeof(GitHubFeedPackageSource))]
    public void ExactlyOneSource_ClaimsEachKnownFeedType(string feedType, Type expected)
    {
        var claimants = AllSources().Where(s => s.CanHandle(feedType)).ToList();

        claimants.Count.ShouldBe(1);
        claimants[0].ShouldBeOfType(expected);
    }

    [Theory]
    [InlineData("Generic")]
    [InlineData("Docker")]
    [InlineData("BuiltIn")]
    [InlineData("")]
    [InlineData(null)]
    public void NoSource_ClaimsUnknownFeedTypes(string feedType)
    {
        AllSources().Any(s => s.CanHandle(feedType)).ShouldBeFalse();
    }

    [Fact]
    public async Task Router_DispatchesToTheClaimingSource_AndNotToTheFallback()
    {
        var claiming = new Mock<IFeedPackageSource>();
        var other = new Mock<IFeedPackageSource>();
        var expected = new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string> { "from-claiming" }, new byte[] { 1 });
        var feed = new ExternalFeed { FeedType = "Custom", FeedUri = "https://x" };

        claiming.Setup(s => s.CanHandle("Custom")).Returns(true);
        claiming.Setup(s => s.FetchAsync(feed, "p", "1", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var factory = new Mock<ISquidHttpClientFactory>();
        var sut = new RoutingPackageContentFetcher(new[] { other.Object, claiming.Object }, new GenericFeedPackageSource(new FeedPackageDownloader(factory.Object)));

        var result = await sut.FetchAsync(feed, "p", "1", CancellationToken.None);

        result.ShouldBeSameAs(expected);
        other.Verify(s => s.FetchAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        factory.Verify(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task Router_UnclaimedFeedType_ReachesGenericFallback_NotAnException()
    {
        var factory = new Mock<ISquidHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Throws(new InvalidOperationException("fallback reached"));
        var sut = PackageFetcherWiring.Create(factory.Object);

        var result = await sut.FetchAsync(new ExternalFeed { Id = 1, FeedType = "BuiltIn", FeedUri = "https://x" }, "p", "1", CancellationToken.None);

        result.Warnings.ShouldBe(new List<string> { "Package fetch failed: fallback reached" });
    }

    [Fact]
    public async Task Router_TwoSourcesClaimingSameType_FailsLoudly()
    {
        var a = new Mock<IFeedPackageSource>();
        var b = new Mock<IFeedPackageSource>();
        a.Setup(s => s.CanHandle(It.IsAny<string>())).Returns(true);
        b.Setup(s => s.CanHandle(It.IsAny<string>())).Returns(true);
        var sut = new RoutingPackageContentFetcher(new[] { a.Object, b.Object }, new GenericFeedPackageSource(new FeedPackageDownloader(Http)));

        await Should.ThrowAsync<InvalidOperationException>(() => sut.FetchAsync(new ExternalFeed { FeedType = "X" }, "p", "1", CancellationToken.None));
    }

    [Fact]
    public void Container_ResolvesSingleRouterAsPackageContentFetcher_WithEverySource()
    {
        // Runs the real SquidModule.RegisterDependency assembly scan: it goes red if the router or
        // any source stops being auto-registered, or if a second IPackageContentFetcher appears.
        var builder = new ContainerBuilder();
        var module = new SquidModule(Serilog.Core.Logger.None, new ConfigurationBuilder().Build());
        typeof(SquidModule).GetMethod("RegisterDependency", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { builder });

        using var container = builder.Build();
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IEnumerable<IPackageContentFetcher>>().Single().ShouldBeOfType<RoutingPackageContentFetcher>();
        scope.Resolve<IEnumerable<IFeedPackageSource>>().Select(s => s.GetType()).OrderBy(t => t.Name).ToArray().ShouldBe(
            new[] { typeof(GitHubFeedPackageSource), typeof(HelmFeedPackageSource), typeof(NuGetFeedPackageSource) });
        scope.Resolve<GenericFeedPackageSource>().ShouldNotBeNull();
    }
}
