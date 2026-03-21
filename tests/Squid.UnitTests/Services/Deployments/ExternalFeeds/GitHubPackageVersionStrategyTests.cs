using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class GitHubPackageVersionStrategyTests
{
    [Theory]
    [InlineData("GitHub", true)]
    [InlineData("GitHub Repository Feed", true)]
    [InlineData("Docker", false)]
    [InlineData("Helm", false)]
    public void CanHandle_ShouldMatchGitHubFeedTypes(string feedType, bool expected)
    {
        var sut = new GitHubPackageVersionStrategy(new Mock<ISquidHttpClientFactory>().Object);

        sut.CanHandle(feedType).ShouldBe(expected);
    }

    [Fact]
    public void ParseReleases_ShouldExtractTagNames()
    {
        var json = """
            [
                {"tag_name": "v1.2.0", "name": "Release 1.2.0"},
                {"tag_name": "v1.1.0", "name": "Release 1.1.0"},
                {"tag_name": "v1.0.0", "name": "Release 1.0.0"}
            ]
            """;

        var result = GitHubPackageVersionStrategy.ParseReleases(json);

        result.Count.ShouldBe(3);
        result[0].ShouldBe("v1.2.0");
        result[1].ShouldBe("v1.1.0");
        result[2].ShouldBe("v1.0.0");
    }

    [Fact]
    public void ParseReleases_EmptyArray_ReturnsEmpty()
    {
        GitHubPackageVersionStrategy.ParseReleases("[]").ShouldBeEmpty();
    }

    [Fact]
    public void ParseReleases_NotArray_ReturnsEmpty()
    {
        GitHubPackageVersionStrategy.ParseReleases("""{"items":[]}""").ShouldBeEmpty();
    }

    [Fact]
    public void ParseReleases_InvalidJson_ReturnsEmpty()
    {
        GitHubPackageVersionStrategy.ParseReleases("bad json").ShouldBeEmpty();
    }
}
