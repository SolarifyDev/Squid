using System;
using System.Linq;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class DockerPackageVersionStrategyTests
{
    [Theory]
    [InlineData("Docker", true)]
    [InlineData("Docker Container Registry", true)]
    [InlineData("AWS Elastic Container Registry", true)]
    [InlineData("Helm", false)]
    [InlineData("GitHub", false)]
    public void CanHandle_ShouldMatchContainerRegistryFeedTypes(string feedType, bool expected)
    {
        var sut = new DockerPackageVersionStrategy(new Mock<ISquidHttpClientFactory>().Object);

        sut.CanHandle(feedType).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://hub.docker.com", true)]
    [InlineData("https://registry-1.docker.io", true)]
    [InlineData("https://index.docker.io", true)]
    [InlineData("https://myregistry.example.com", false)]
    [InlineData("https://harbor.internal.io", false)]
    public void IsDockerHub_ShouldDetectDockerHubHosts(string url, bool expected)
    {
        DockerPackageVersionStrategy.IsDockerHub(new Uri(url)).ShouldBe(expected);
    }

    [Fact]
    public void ParseRegistryTags_ShouldExtractTags()
    {
        var json = """
            {
                "name": "my-app/nginx",
                "tags": ["latest", "1.0.0", "1.0.1", "1.1.0"]
            }
            """;

        var result = DockerPackageVersionStrategy.ParseRegistryTags(json);

        result.Count.ShouldBe(4);
        result.ShouldContain("latest");
        result.ShouldContain("1.0.0");
        result.ShouldContain("1.1.0");
    }

    [Fact]
    public void ParseRegistryTags_ShouldIncludePreReleaseTags()
    {
        var json = """{"tags": ["1.0.0", "1.0.1-rc1", "1.0.1-beta.2", "2.0.0-alpha", "latest"]}""";

        var result = DockerPackageVersionStrategy.ParseRegistryTags(json);

        result.Count.ShouldBe(5);
        result.ShouldContain("1.0.1-rc1");
        result.ShouldContain("1.0.1-beta.2");
        result.ShouldContain("2.0.0-alpha");
    }

    /// <summary>
    /// Inverted version of the deleted "ShouldRespectTakeLimit" test. The new
    /// contract: the parser returns ALL tags from the page payload — semver sort
    /// + take is the orchestrator's job (<see cref="PackageVersionFilter.Apply"/>).
    /// If this fails it likely means someone re-introduced pre-sort truncation
    /// here, which would re-open the production bug where freshly pushed lex-late
    /// versions (e.g. 1.1.0 after 1.0.3-8) never appear in the dropdown.
    /// </summary>
    [Fact]
    public void ParseRegistryTags_ShouldReturnAllTagsFromPage_NoTruncation()
    {
        // 50 tags — well over any historical pre-sort take=30 limit.
        var tags = string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"v{i}\""));
        var json = $"{{\"tags\": [{tags}]}}";

        var result = DockerPackageVersionStrategy.ParseRegistryTags(json);

        result.Count.ShouldBe(50,
            customMessage: "Parser MUST return all tags — order + take belong to PackageVersionFilter.Apply. " +
                          "If this fails, the production bug 'freshly pushed 1.1.0 hidden behind 30 lex-earlier " +
                          "1.0.x-N tags' has been re-introduced.");
    }

    [Fact]
    public void ParseRegistryTags_NullTags_ReturnsEmpty()
    {
        var json = """{"name": "my-app"}""";

        DockerPackageVersionStrategy.ParseRegistryTags(json).ShouldBeEmpty();
    }

    [Fact]
    public void ParseRegistryTags_InvalidJson_ReturnsEmpty()
    {
        DockerPackageVersionStrategy.ParseRegistryTags("bad").ShouldBeEmpty();
    }

    [Fact]
    public void MaxTagsPerPage_IsAt100()
    {
        // Pinned: per Docker registry v2 spec the per-page upper bound is ~100;
        // changing this affects round-trip count for every Docker feed in production.
        DockerPackageVersionStrategy.MaxTagsPerPage.ShouldBe(100);
    }
}
