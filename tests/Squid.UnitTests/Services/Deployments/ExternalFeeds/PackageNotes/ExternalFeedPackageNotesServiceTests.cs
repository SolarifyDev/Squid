using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds.PackageNotes;

public class ExternalFeedPackageNotesServiceTests
{
    private readonly Mock<IExternalFeedDataProvider> _dataProvider = new();

    private ExternalFeedPackageNotesService CreateService(params IPackageNotesStrategy[] strategies) =>
        new(_dataProvider.Object, strategies);

    [Fact]
    public async Task GetNotesAsync_SelectsCorrectStrategy()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker Registry", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var strategy = new Mock<IPackageNotesStrategy>();
        strategy.Setup(x => x.CanHandle("Docker Registry")).Returns(true);
        strategy.Setup(x => x.GetNotesAsync(feed, "myimage", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageNotesResult.Success("Platform: linux amd64"));

        var service = CreateService(strategy.Object);
        var queries = new List<PackageNotesQuery> { new() { FeedId = 1, PackageId = "myimage", Version = "latest" } };

        var result = await service.GetNotesAsync(queries, CancellationToken.None);

        result.Packages.Count.ShouldBe(1);
        result.Packages[0].Succeeded.ShouldBeTrue();
        result.Packages[0].Notes.ShouldBe("Platform: linux amd64");
    }

    [Fact]
    public async Task GetNotesAsync_FeedNotFound_ReturnsPerPackageFailure()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((ExternalFeed)null);

        var service = CreateService();
        var queries = new List<PackageNotesQuery> { new() { FeedId = 999, PackageId = "pkg", Version = "1.0" } };

        var result = await service.GetNotesAsync(queries, CancellationToken.None);

        result.Packages.Count.ShouldBe(1);
        result.Packages[0].Succeeded.ShouldBeFalse();
        result.Packages[0].FailureReason.ShouldBe("Feed not found");
    }

    [Fact]
    public async Task GetNotesAsync_NoMatchingStrategy_ReturnsUnsupportedFeedType()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "UnknownFeed", FeedUri = "https://example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var service = CreateService();
        var queries = new List<PackageNotesQuery> { new() { FeedId = 1, PackageId = "pkg", Version = "1.0" } };

        var result = await service.GetNotesAsync(queries, CancellationToken.None);

        result.Packages.Count.ShouldBe(1);
        result.Packages[0].Succeeded.ShouldBeFalse();
        result.Packages[0].FailureReason.ShouldContain("Unsupported feed type");
    }

    [Fact]
    public async Task GetNotesAsync_BatchAcrossMultipleFeeds_RoutesCorrectly()
    {
        var dockerFeed = new ExternalFeed { Id = 1, FeedType = "Docker Registry", FeedUri = "https://docker.io" };
        var githubFeed = new ExternalFeed { Id = 2, FeedType = "GitHub", FeedUri = "https://github.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(dockerFeed);
        _dataProvider.Setup(x => x.GetFeedByIdAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(githubFeed);

        var dockerStrategy = new Mock<IPackageNotesStrategy>();
        dockerStrategy.Setup(x => x.CanHandle("Docker Registry")).Returns(true);
        dockerStrategy.Setup(x => x.GetNotesAsync(dockerFeed, "myimage", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageNotesResult.Success("Platform: linux amd64"));

        var githubStrategy = new Mock<IPackageNotesStrategy>();
        githubStrategy.Setup(x => x.CanHandle("GitHub")).Returns(true);
        githubStrategy.Setup(x => x.GetNotesAsync(githubFeed, "owner/repo", "v2.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageNotesResult.Success("Release notes here"));

        var service = CreateService(dockerStrategy.Object, githubStrategy.Object);
        var queries = new List<PackageNotesQuery>
        {
            new() { FeedId = 1, PackageId = "myimage", Version = "v1" },
            new() { FeedId = 2, PackageId = "owner/repo", Version = "v2.0" }
        };

        var result = await service.GetNotesAsync(queries, CancellationToken.None);

        result.Packages.Count.ShouldBe(2);
        result.Packages.ShouldContain(p => p.FeedId == 1 && p.Notes == "Platform: linux amd64");
        result.Packages.ShouldContain(p => p.FeedId == 2 && p.Notes == "Release notes here");
    }

    [Fact]
    public async Task GetNotesAsync_OnePackageFails_OthersContinue()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker Registry", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var strategy = new Mock<IPackageNotesStrategy>();
        strategy.Setup(x => x.CanHandle("Docker Registry")).Returns(true);
        strategy.Setup(x => x.GetNotesAsync(feed, "good-image", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageNotesResult.Success("Platform: linux amd64"));
        strategy.Setup(x => x.GetNotesAsync(feed, "bad-image", "v1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Network error"));

        var service = CreateService(strategy.Object);
        var queries = new List<PackageNotesQuery>
        {
            new() { FeedId = 1, PackageId = "good-image", Version = "v1" },
            new() { FeedId = 1, PackageId = "bad-image", Version = "v1" }
        };

        var result = await service.GetNotesAsync(queries, CancellationToken.None);

        result.Packages.Count.ShouldBe(2);
        result.Packages.ShouldContain(p => p.PackageId == "good-image" && p.Succeeded);
        result.Packages.ShouldContain(p => p.PackageId == "bad-image" && !p.Succeeded && p.FailureReason == "Network error");
    }

    [Fact]
    public async Task GetNotesAsync_EmptyQueries_ReturnsEmptyResult()
    {
        var service = CreateService();

        var result = await service.GetNotesAsync(new List<PackageNotesQuery>(), CancellationToken.None);

        result.Packages.ShouldBeEmpty();
    }
}
