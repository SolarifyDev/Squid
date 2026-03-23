using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class ExternalFeedPackageVersionServiceTests
{
    private readonly Mock<IExternalFeedDataProvider> _dataProvider = new();
    private readonly Mock<IPackageVersionStrategy> _dockerStrategy = new();
    private readonly Mock<IPackageVersionStrategy> _helmStrategy = new();

    public ExternalFeedPackageVersionServiceTests()
    {
        _dockerStrategy.Setup(x => x.CanHandle("Docker")).Returns(true);
        _helmStrategy.Setup(x => x.CanHandle("Helm")).Returns(true);
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldSelectCorrectStrategy()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "nginx", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["1.25.4", "1.25.3", "latest"]);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "nginx", 30, true, null, CancellationToken.None);

        result.Versions.Count.ShouldBe(3);
        _dockerStrategy.Verify(x => x.ListVersionsAsync(feed, "nginx", 30, It.IsAny<CancellationToken>()), Times.Once);
        _helmStrategy.Verify(x => x.ListVersionsAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldReturnEmpty_WhenFeedNotFound()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((ExternalFeed)null);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(99, "nginx", 30, false, null, CancellationToken.None);

        result.Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldReturnEmpty_WhenFeedUriMissing()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "nginx", 30, false, null, CancellationToken.None);

        result.Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldReturnEmpty_WhenNoStrategyMatches()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "NuGet", FeedUri = "https://nuget.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "test", 30, false, null, CancellationToken.None);

        result.Versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldFilterPreRelease_WhenNotIncluded()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["7.2.4", "7.2.4-rc1", "7.2.3", "7.2.3-beta.2", "7.2.2", "latest"]);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "redis", 30, false, null, CancellationToken.None);

        result.Versions.ShouldNotContain("7.2.4-rc1");
        result.Versions.ShouldNotContain("7.2.3-beta.2");
        result.Versions.ShouldContain("7.2.4");
        result.Versions.ShouldContain("7.2.3");
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldKeepAllVersions_WhenIncludePreRelease()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["7.2.4", "7.2.4-rc1", "7.2.3-beta.2", "latest"]);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "redis", 30, true, null, CancellationToken.None);

        result.Versions.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldFilterBySearchTerm()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["7.2.4", "7.2.3", "7.0.15", "6.2.14", "latest"]);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "redis", 30, true, "7.2", CancellationToken.None);

        result.Versions.ShouldBe(["7.2.4", "7.2.3"]);
    }

    [Fact]
    public async Task ListVersionsAsync_ShouldSortByVersionDescending()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["6.2.14", "7.0.15", "7.2.4", "7.2.3"]);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "redis", 30, true, null, CancellationToken.None);

        result.Versions[0].ShouldBe("7.2.4");
        result.Versions[1].ShouldBe("7.2.3");
        result.Versions[2].ShouldBe("7.0.15");
        result.Versions[3].ShouldBe("6.2.14");
    }

    private ExternalFeedPackageVersionService CreateSut()
    {
        var strategies = new IPackageVersionStrategy[] { _dockerStrategy.Object, _helmStrategy.Object };

        return new ExternalFeedPackageVersionService(_dataProvider.Object, strategies);
    }
}
