using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class DockerPackageSearchStrategyTests
{
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();

    [Theory]
    [InlineData("Docker")]
    [InlineData("Docker Container Registry")]
    [InlineData("AWS Elastic Container Registry")]
    [InlineData("OCI Registry Feed")]
    public void CanHandle_ShouldReturnTrue_ForContainerRegistryFeedTypes(string feedType)
    {
        var sut = CreateSut();

        sut.CanHandle(feedType).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Helm")]
    [InlineData("GitHub")]
    [InlineData("NuGet")]
    public void CanHandle_ShouldReturnFalse_ForNonDockerFeedTypes(string feedType)
    {
        var sut = CreateSut();

        sut.CanHandle(feedType).ShouldBeFalse();
    }

    [Fact]
    public async Task SearchAsync_DockerHub_ShouldParseSearchResults()
    {
        var requestedUrl = default(string);

        var client = CreateHttpClient((request, _) =>
        {
            requestedUrl = request.RequestUri.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                        "results": [
                            {"repo_name": "library/nginx"},
                            {"repo_name": "bitnami/nginx"},
                            {"repo_name": "nginx/nginx-ingress"}
                        ]
                    }
                    """)
            });
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Docker", FeedUri = "https://index.docker.io" };

        var result = await sut.SearchAsync(feed, "nginx", 10, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(3);
        result.ShouldContain("library/nginx");
        result.ShouldContain("bitnami/nginx");
        result.ShouldContain("nginx/nginx-ingress");
        requestedUrl.ShouldContain("hub.docker.com/v2/search/repositories");
        requestedUrl.ShouldContain("query=nginx");
    }

    [Fact]
    public async Task SearchAsync_DockerHub_WithRegistryPath_ShouldScopeToNamespace()
    {
        var requestedUrl = default(string);

        var client = CreateHttpClient((request, _) =>
        {
            requestedUrl = request.RequestUri.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                        "results": [
                            {"name": "squid-tentacle"},
                            {"name": "kubernetes-agent"}
                        ]
                    }
                    """)
            });
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Docker", FeedUri = "https://index.docker.io", Username = "squidcd" };

        var result = await sut.SearchAsync(feed, "squid", 10, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.ShouldContain("squidcd/squid-tentacle");
        result.ShouldContain("squidcd/kubernetes-agent");
        requestedUrl.ShouldContain("hub.docker.com/v2/namespaces/squidcd/repositories");
        requestedUrl.ShouldContain("name=squid");
        requestedUrl.ShouldNotContain("search/repositories");
    }

    [Fact]
    public async Task SearchAsync_DockerHub_ShouldReturnEmpty_WhenApiFails()
    {
        var client = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Docker", FeedUri = "https://hub.docker.com" };

        var result = await sut.SearchAsync(feed, "nginx", 10, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_GenericRegistry_ShouldQueryCatalog()
    {
        var requestedUrl = default(string);

        var client = CreateHttpClient((request, _) =>
        {
            requestedUrl = request.RequestUri.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                        "repositories": ["my-app/nginx", "my-app/redis", "my-app/postgres", "tools/alpine"]
                    }
                    """)
            });
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Docker", FeedUri = "https://registry.example.com" };

        var result = await sut.SearchAsync(feed, "nginx", 10, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result.ShouldContain("my-app/nginx");
        requestedUrl.ShouldContain("_catalog");
    }

    [Fact]
    public async Task SearchAsync_GenericRegistry_ShouldUseBearerToken_When401WithChallenge()
    {
        var requestCount = 0;
        string capturedBearerAuth = null;

        var client = CreateHttpClient((request, _) =>
        {
            requestCount++;

            if (request.RequestUri.Host == "registry.example.com" && request.Headers.Authorization?.Scheme != "Bearer")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer",
                    "realm=\"https://auth.example.com/token\",service=\"registry.example.com\""));
                return Task.FromResult(challenge);
            }

            if (request.RequestUri.Host == "auth.example.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"token\":\"test-bearer-token\"}")
                });
            }

            if (request.RequestUri.Host == "registry.example.com" && request.Headers.Authorization?.Scheme == "Bearer")
            {
                capturedBearerAuth = request.Headers.Authorization.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"repositories\":[\"app/nginx\",\"app/redis\"]}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Docker", FeedUri = "https://registry.example.com", Username = "user", Password = "pass" };

        var result = await sut.SearchAsync(feed, "nginx", 10, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldContain("app/nginx");
        capturedBearerAuth.ShouldBe("test-bearer-token");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnEmpty_WhenFeedUriInvalid()
    {
        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "Docker", FeedUri = "not-a-url" };

        var result = await sut.SearchAsync(feed, "nginx", 10, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    private DockerPackageSearchStrategy CreateSut() => new(_httpClientFactory.Object);

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new DelegatingStubHandler(handler));

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
