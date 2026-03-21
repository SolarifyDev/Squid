using System;
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

        var result = DockerPackageVersionStrategy.ParseRegistryTags(json, 100);

        result.Count.ShouldBe(4);
        result.ShouldContain("latest");
        result.ShouldContain("1.0.0");
        result.ShouldContain("1.1.0");
    }

    [Fact]
    public void ParseRegistryTags_ShouldIncludePreReleaseTags()
    {
        var json = """{"tags": ["1.0.0", "1.0.1-rc1", "1.0.1-beta.2", "2.0.0-alpha", "latest"]}""";

        var result = DockerPackageVersionStrategy.ParseRegistryTags(json, 100);

        result.Count.ShouldBe(5);
        result.ShouldContain("1.0.1-rc1");
        result.ShouldContain("1.0.1-beta.2");
        result.ShouldContain("2.0.0-alpha");
    }

    [Fact]
    public void ParseRegistryTags_ShouldRespectTakeLimit()
    {
        var json = """{"tags": ["v1", "v2", "v3", "v4", "v5"]}""";

        var result = DockerPackageVersionStrategy.ParseRegistryTags(json, 3);

        result.Count.ShouldBe(3);
    }

    [Fact]
    public void ParseRegistryTags_NullTags_ReturnsEmpty()
    {
        var json = """{"name": "my-app"}""";

        DockerPackageVersionStrategy.ParseRegistryTags(json, 100).ShouldBeEmpty();
    }

    [Fact]
    public void ParseRegistryTags_InvalidJson_ReturnsEmpty()
    {
        DockerPackageVersionStrategy.ParseRegistryTags("bad", 100).ShouldBeEmpty();
    }
}
