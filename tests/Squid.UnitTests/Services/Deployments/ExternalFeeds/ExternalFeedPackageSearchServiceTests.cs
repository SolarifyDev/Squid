using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class ExternalFeedPackageSearchServiceTests
{
    private readonly Mock<IExternalFeedDataProvider> _dataProvider = new();
    private readonly Mock<IPackageSearchStrategy> _dockerStrategy = new();
    private readonly Mock<IPackageSearchStrategy> _helmStrategy = new();

    public ExternalFeedPackageSearchServiceTests()
    {
        _dockerStrategy.Setup(x => x.CanHandle("Docker")).Returns(true);
        _helmStrategy.Setup(x => x.CanHandle("Helm")).Returns(true);
    }

    [Fact]
    public async Task SearchAsync_ShouldSelectCorrectStrategy()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        _dockerStrategy.Setup(x => x.SearchAsync(feed, "nginx", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["library/nginx", "bitnami/nginx"]);

        var sut = CreateSut();

        var result = await sut.SearchAsync(1, "nginx", 10, CancellationToken.None);

        result.Packages.ShouldNotBeNull();
        result.Packages.Count.ShouldBe(2);
        _dockerStrategy.Verify(x => x.SearchAsync(feed, "nginx", 10, It.IsAny<CancellationToken>()), Times.Once);
        _helmStrategy.Verify(x => x.SearchAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenFeedNotFound()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((ExternalFeed)null);

        var sut = CreateSut();

        var result = await sut.SearchAsync(99, "nginx", 10, CancellationToken.None);

        result.Packages.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenFeedUriMissing()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var sut = CreateSut();

        var result = await sut.SearchAsync(1, "nginx", 10, CancellationToken.None);

        result.Packages.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenNoStrategyMatches()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "NuGet", FeedUri = "https://nuget.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var sut = CreateSut();

        var result = await sut.SearchAsync(1, "test", 10, CancellationToken.None);

        result.Packages.ShouldBeEmpty();
    }

    private ExternalFeedPackageSearchService CreateSut()
    {
        var strategies = new IPackageSearchStrategy[] { _dockerStrategy.Object, _helmStrategy.Object };

        return new ExternalFeedPackageSearchService(_dataProvider.Object, strategies);
    }
}
