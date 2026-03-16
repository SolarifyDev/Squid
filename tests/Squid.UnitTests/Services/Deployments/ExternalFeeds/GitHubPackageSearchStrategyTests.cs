using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class GitHubPackageSearchStrategyTests
{
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();

    [Theory]
    [InlineData("GitHub", true)]
    [InlineData("GitHub Repository Feed", true)]
    [InlineData("Docker", false)]
    [InlineData("Helm", false)]
    public void CanHandle_ShouldMatchGitHubFeedTypes(string feedType, bool expected)
    {
        var sut = CreateSut();

        sut.CanHandle(feedType).ShouldBe(expected);
    }

    [Fact]
    public async Task SearchAsync_ShouldParseGitHubApiResults()
    {
        string capturedAuthHeader = null;

        var client = CreateHttpClient((request, _) =>
        {
            capturedAuthHeader = request.Headers.Authorization?.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                        "total_count": 2,
                        "items": [
                            {"full_name": "kubernetes/kubernetes"},
                            {"full_name": "kubernetes-sigs/kind"}
                        ]
                    }
                    """)
            });
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "GitHub", FeedUri = "https://api.github.com", Password = "ghp_token123" };

        var result = await sut.SearchAsync(feed, "kubernetes", 10, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.ShouldContain("kubernetes/kubernetes");
        result.ShouldContain("kubernetes-sigs/kind");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenApiFails()
    {
        var client = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "GitHub", FeedUri = "https://api.github.com" };

        var result = await sut.SearchAsync(feed, "test", 10, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ShouldIncludeUserAgentHeader()
    {
        Dictionary<string, string> capturedHeaders = null;

        var client = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}")
            }));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<TimeSpan?, bool, Dictionary<string, string>>((_, _, headers) => capturedHeaders = headers)
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "GitHub", FeedUri = "https://api.github.com" };

        await sut.SearchAsync(feed, "test", 10, CancellationToken.None);

        capturedHeaders.ShouldNotBeNull();
        capturedHeaders.ShouldContainKey("User-Agent");
        capturedHeaders["User-Agent"].ShouldBe("Squid");
    }

    [Fact]
    public async Task SearchAsync_ShouldIncludeTokenAuth_WhenPasswordProvided()
    {
        Dictionary<string, string> capturedHeaders = null;

        var client = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}")
            }));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<TimeSpan?, bool, Dictionary<string, string>>((_, _, headers) => capturedHeaders = headers)
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "GitHub", FeedUri = "https://api.github.com", Password = "ghp_mytoken" };

        await sut.SearchAsync(feed, "test", 10, CancellationToken.None);

        capturedHeaders.ShouldContainKey("Authorization");
        capturedHeaders["Authorization"].ShouldBe("token ghp_mytoken");
    }

    private GitHubPackageSearchStrategy CreateSut() => new(_httpClientFactory.Object);

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new DelegatingStubHandler(handler));

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
