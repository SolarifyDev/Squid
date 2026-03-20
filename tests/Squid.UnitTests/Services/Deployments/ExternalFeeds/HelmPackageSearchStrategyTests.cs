using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class HelmPackageSearchStrategyTests
{
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();

    [Theory]
    [InlineData("Helm", true)]
    [InlineData("Helm Feed", true)]
    [InlineData("Docker", false)]
    [InlineData("GitHub", false)]
    public void CanHandle_ShouldMatchHelmFeedTypes(string feedType, bool expected)
    {
        var sut = CreateSut();

        sut.CanHandle(feedType).ShouldBe(expected);
    }

    [Fact]
    public async Task SearchAsync_ShouldParseChartNamesFromIndexYaml()
    {
        var yaml = """
            apiVersion: v1
            entries:
              nginx:
                - version: 1.0.0
                  description: nginx chart
              redis:
                - version: 2.0.0
                  description: redis chart
              nginx-ingress:
                - version: 3.0.0
                  description: nginx ingress chart
              postgres:
                - version: 4.0.0
                  description: postgres chart
            generated: "2024-01-01T00:00:00Z"
            """;

        var client = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(yaml)
            }));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Helm", FeedUri = "https://helm.example.com/charts" };

        var result = await sut.SearchAsync(feed, "nginx", 10, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.ShouldContain("nginx");
        result.ShouldContain("nginx-ingress");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenHttpFails()
    {
        var client = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Helm", FeedUri = "https://helm.example.com/charts" };

        var result = await sut.SearchAsync(feed, "nginx", 10, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ShouldRespectTakeLimit()
    {
        var yaml = """
            apiVersion: v1
            entries:
              chart-a:
                - version: 1.0.0
              chart-b:
                - version: 1.0.0
              chart-c:
                - version: 1.0.0
            """;

        var client = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(yaml)
            }));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Helm", FeedUri = "https://helm.example.com" };

        var result = await sut.SearchAsync(feed, "", 2, CancellationToken.None);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseChartNames_ShouldHandleEmptyQuery()
    {
        var yaml = """
            entries:
              nginx:
                - version: 1.0.0
              redis:
                - version: 2.0.0
            """;

        var result = HelmPackageSearchStrategy.ParseChartNames(yaml, "", 10);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseChartNames_ShouldStopAtNonEntriesSection()
    {
        var yaml = """
            entries:
              nginx:
                - version: 1.0.0
            generated: "2024-01-01"
            """;

        var result = HelmPackageSearchStrategy.ParseChartNames(yaml, "", 10);

        result.Count.ShouldBe(1);
        result.ShouldContain("nginx");
    }

    private HelmPackageSearchStrategy CreateSut() => new(_httpClientFactory.Object);

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new DelegatingStubHandler(handler));

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
