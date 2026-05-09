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
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "nginx", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["1.25.4", "1.25.3", "latest"]);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "nginx", 30, true, null, CancellationToken.None);

        result.Versions.Count.ShouldBe(3);
        _dockerStrategy.Verify(x => x.ListVersionsAsync(feed, "nginx", It.IsAny<CancellationToken>()), Times.Once);
        _helmStrategy.Verify(x => x.ListVersionsAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", It.IsAny<CancellationToken>()))
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
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", It.IsAny<CancellationToken>()))
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
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", It.IsAny<CancellationToken>()))
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
        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "redis", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["6.2.14", "7.0.15", "7.2.4", "7.2.3"]);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "redis", 30, true, null, CancellationToken.None);

        result.Versions[0].ShouldBe("7.2.4");
        result.Versions[1].ShouldBe("7.2.3");
        result.Versions[2].ShouldBe("7.0.15");
        result.Versions[3].ShouldBe("6.2.14");
    }

    /// <summary>
    /// Pinned regression for the real production bug: a user pushed Docker image
    /// <c>1.1.0</c> after a long history of <c>1.0.x-N</c> tags. Docker registries
    /// return tags lex-sorted, so <c>1.0.3-8</c> sorts before <c>1.1.0</c> (the
    /// '0' at the third character is less than '1'). With the old contract the
    /// strategy truncated to 30 tags BEFORE semver sort — the user's freshly
    /// pushed <c>1.1.0</c> was never seen.
    ///
    /// <para>Pre-fix: the strategy received <c>take=30</c> and stopped reading
    /// tags at position 30; <c>1.1.0</c> at position 31 was lost. Post-fix:
    /// strategy returns ALL 32 tags, PackageVersionFilter sorts by semver, take
    /// keeps the top 30 — <c>1.1.0</c> sits at position #1.</para>
    /// </summary>
    [Fact]
    public async Task ListVersionsAsync_ShouldNotHideNewerVersionBehindLexSort()
    {
        var feed = new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "https://registry.example.com" };
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(feed);

        // Mimic the user's actual feed: 31 tags ending at "1.0.3-8" (lex-late-but-
        // semver-old) plus the freshly pushed "1.1.0". The strategy returns them
        // in lex order — exactly what `/v2/.../tags/list` produces.
        var lexOrderedTags = new List<string>
        {
            "1.0.0", "1.0.1", "1.0.1-1", "1.0.1-2", "1.0.2", "1.0.2-1", "1.0.2-2",
            "1.0.2-3", "1.0.2-4", "1.0.2-5", "1.0.2-6", "1.0.2-7", "1.0.2-8",
            "1.0.2-9", "1.0.3", "1.0.3-1", "1.0.3-2", "1.0.3-3", "1.0.3-4",
            "1.0.3-5", "1.0.3-6", "1.0.3-7", "1.0.3-8", "1.0.3-9", "1.0.3-10",
            "1.0.3-11", "1.0.3-12", "1.0.3-13", "1.0.3-14", "1.0.3-15", "1.0.3-16",
            "1.1.0"
        };

        _dockerStrategy.Setup(x => x.ListVersionsAsync(feed, "web", It.IsAny<CancellationToken>()))
            .ReturnsAsync(lexOrderedTags);

        var sut = CreateSut();

        var result = await sut.ListVersionsAsync(1, "web", 30, includePreRelease: true, filter: null, CancellationToken.None);

        result.Versions.Count.ShouldBe(30);
        result.Versions[0].ShouldBe("1.1.0",
            customMessage: "Top result MUST be 1.1.0 — the strategy now returns ALL upstream tags " +
                          "and PackageVersionFilter applies semver sort BEFORE take. " +
                          "If this fails: someone re-introduced pre-sort truncation in the " +
                          "strategy or service. See ExternalFeedPackageVersionService for the " +
                          "correct order: ListVersionsAsync(no take) → PackageVersionFilter.Apply(take).");
    }

    private ExternalFeedPackageVersionService CreateSut()
    {
        var strategies = new IPackageVersionStrategy[] { _dockerStrategy.Object, _helmStrategy.Object };

        return new ExternalFeedPackageVersionService(_dataProvider.Object, strategies);
    }
}
