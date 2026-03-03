using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class ExternalFeedConnectionTestServiceTests
{
    private static readonly IReadOnlyList<IExternalFeedProbeRule<ExternalFeedProbePlan>> ProbeRules =
    [
        new ExternalFeedDockerProbeRule(),
        new ExternalFeedGitHubProbeRule(),
        new ExternalFeedHelmProbeRule(),
        new ExternalFeedDefaultProbeRule()
    ];

    private readonly Mock<IExternalFeedDataProvider> _dataProvider = new();
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();

    [Fact]
    public async Task TestAsync_ReturnsNotFound_WhenFeedDoesNotExist()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalFeed)null);

        var sut = CreateSut();

        var result = await sut.TestAsync(42, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Feed not found.");
        _httpClientFactory.Verify(
            x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task TestAsync_ReturnsValidationError_WhenFeedUriMissing()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 1, FeedUri = "  " });

        var sut = CreateSut();

        var result = await sut.TestAsync(1, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Feed URL is not configured.");
    }

    [Fact]
    public async Task TestAsync_ReturnsValidationError_WhenFeedUriInvalid()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 1, FeedType = "Docker", FeedUri = "not-a-url" });

        var sut = CreateSut();

        var result = await sut.TestAsync(1, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Feed URL is invalid.");
    }

    [Fact]
    public async Task TestAsync_UsesDockerProbeRule_AndTreats401AsReachable()
    {
        var requestedUri = default(Uri);
        TimeSpan? configuredTimeout = null;
        Dictionary<string, string> configuredHeaders = null;

        _dataProvider.Setup(x => x.GetFeedByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 1,
                FeedType = "Docker",
                FeedUri = "https://registry.example.com",
                Username = "user",
                Password = "pass"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<TimeSpan?, bool, Dictionary<string, string>>((timeout, _, headers) =>
            {
                configuredTimeout = timeout;
                configuredHeaders = headers;
            })
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(1, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Connected successfully (HTTP 401).");
        requestedUri.ShouldBe(new Uri("https://registry.example.com/v2"));
        configuredTimeout.ShouldBe(TimeSpan.FromSeconds(30));

        configuredHeaders.ShouldNotBeNull();
        configuredHeaders.ContainsKey("Authorization").ShouldBeTrue();
        configuredHeaders["Authorization"]
            .ShouldBe($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"))}");
    }

    [Fact]
    public async Task TestAsync_DockerFeedWithV2Suffix_ShouldNotDuplicatePath()
    {
        var requestedUri = default(Uri);

        _dataProvider.Setup(x => x.GetFeedByIdAsync(11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 11,
                FeedType = "Docker",
                FeedUri = "https://index.docker.io/v2"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(11, CancellationToken.None);

        result.Success.ShouldBeTrue();
        requestedUri.ShouldBe(new Uri("https://index.docker.io/v2/"));
    }

    [Fact]
    public async Task TestAsync_DockerFeed_ShouldFallbackFromV2ToV1()
    {
        var requestedUris = new List<string>();

        _dataProvider.Setup(x => x.GetFeedByIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 12,
                FeedType = "Docker",
                FeedUri = "https://registry.example.com"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUris.Add(request.RequestUri.AbsoluteUri);

            if (request.RequestUri.AbsolutePath.EndsWith("/v2", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    ReasonPhrase = "Not Found"
                });
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(12, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Connected successfully (HTTP 401).");
        requestedUris.ShouldBe(
        [
            "https://registry.example.com/v2",
            "https://registry.example.com/v1"
        ]);
    }

    [Fact]
    public async Task TestAsync_GitHubFeed_ShouldProbeBaseUri()
    {
        var requestedUri = default(Uri);

        _dataProvider.Setup(x => x.GetFeedByIdAsync(13, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 13,
                FeedType = "GitHub",
                FeedUri = "https://api.github.com"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(13, CancellationToken.None);

        result.Success.ShouldBeTrue();
        requestedUri.ShouldBe(new Uri("https://api.github.com/"));
    }

    [Fact]
    public async Task TestAsync_DockerFeedWithApiVersion_ShouldRespectApiVersion()
    {
        var requestedUris = new List<string>();

        _dataProvider.Setup(x => x.GetFeedByIdAsync(14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 14,
                FeedType = "Docker",
                FeedUri = "https://registry.example.com",
                ApiVersion = "v1"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUris.Add(request.RequestUri.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(14, CancellationToken.None);

        result.Success.ShouldBeTrue();
        requestedUris.ShouldBe(["https://registry.example.com/v1"]);
    }

    [Theory]
    [InlineData("AWS Elastic Container Registry")]
    [InlineData("Azure Container Registry")]
    [InlineData("Google Container Registry")]
    [InlineData("OCI Registry Feed")]
    public async Task TestAsync_ContainerRegistryLikeFeedTypes_ShouldUseDockerStyleProbe(string feedType)
    {
        var requestedUri = default(Uri);

        _dataProvider.Setup(x => x.GetFeedByIdAsync(15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 15,
                FeedType = feedType,
                FeedUri = "https://registry.example.com"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(15, CancellationToken.None);

        result.Success.ShouldBeTrue();
        requestedUri.ShouldBe(new Uri("https://registry.example.com/v2"));
    }

    [Fact]
    public async Task TestAsync_UsesHelmProbeRule_AndTreats404AsUnreachable()
    {
        var requestedUri = default(Uri);

        _dataProvider.Setup(x => x.GetFeedByIdAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 2,
                FeedType = "Helm",
                FeedUri = "https://helm.example.com/charts"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                ReasonPhrase = "Not Found"
            });
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(2, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Server responded with HTTP 404 Not Found.");
        requestedUri.ShouldBe(new Uri("https://helm.example.com/charts/index.yaml"));
    }

    [Fact]
    public async Task TestAsync_UsesDefaultProbeRule_ForUnknownFeedType()
    {
        var requestedUri = default(Uri);

        _dataProvider.Setup(x => x.GetFeedByIdAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 3,
                FeedType = "NuGet",
                FeedUri = "https://packages.example.com/feed"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(3, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Connected successfully (HTTP 200).");
        requestedUri.ShouldBe(new Uri("https://packages.example.com/feed/"));
    }

    [Theory]
    [InlineData("Artifactory Generic Feed")]
    [InlineData("AWS S3 Bucket Feed")]
    [InlineData("Maven Feed")]
    [InlineData("NuGet Feed")]
    public async Task TestAsync_NonRegistryAndNonSpecialFeedTypes_ShouldUseDefaultProbeRule(string feedType)
    {
        var requestedUri = default(Uri);

        _dataProvider.Setup(x => x.GetFeedByIdAsync(16, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 16,
                FeedType = feedType,
                FeedUri = "https://packages.example.com/feed"
            });

        var client = CreateHttpClient((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(16, CancellationToken.None);

        result.Success.ShouldBeTrue();
        requestedUri.ShouldBe(new Uri("https://packages.example.com/feed/"));
    }

    [Fact]
    public async Task TestAsync_ReturnsTimeoutMessage_WhenRequestTimesOut()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 4,
                FeedType = "Docker",
                FeedUri = "https://registry.example.com"
            });

        var client = CreateHttpClient((_, _) => throw new OperationCanceledException());

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(4, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Connection timed out.");
    }

    [Fact]
    public async Task TestAsync_PropagatesCancellation_WhenCallerTokenIsCanceled()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 5,
                FeedType = "Docker",
                FeedUri = "https://registry.example.com"
            });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = CreateHttpClient((_, token) => throw new OperationCanceledException(token));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        await Should.ThrowAsync<OperationCanceledException>(() => sut.TestAsync(5, cts.Token));
    }

    [Fact]
    public async Task TestAsync_ReturnsConnectionError_WhenHttpRequestFails()
    {
        _dataProvider.Setup(x => x.GetFeedByIdAsync(6, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 6,
                FeedType = "Docker",
                FeedUri = "https://registry.example.com"
            });

        var client = CreateHttpClient((_, _) => throw new HttpRequestException("network down"));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();

        var result = await sut.TestAsync(6, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Connection failed: network down");
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/root", "https://example.com/root/")]
    public void TryNormalize_ShouldEnsureTrailingSlash(string input, string expected)
    {
        var success = ExternalFeedProbeUri.TryNormalize(input, out var normalized);

        success.ShouldBeTrue();
        normalized.ShouldBe(new Uri(expected));
    }

    [Fact]
    public void TryNormalize_ShouldRejectUnsupportedScheme()
    {
        var success = ExternalFeedProbeUri.TryNormalize("ftp://example.com", out var normalized);

        success.ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    [Fact]
    public void AppendPath_ShouldCombinePathCorrectly()
    {
        ExternalFeedProbeUri.TryNormalize("https://example.com/feed", out var baseUri).ShouldBeTrue();

        var probeUri = ExternalFeedProbeUri.AppendPath(baseUri, "/v2/");

        probeUri.ShouldBe(new Uri("https://example.com/feed/v2/"));
    }

    [Fact]
    public void EnsureEndsWithPathSegment_ShouldNotDuplicateSegment()
    {
        ExternalFeedProbeUri.TryNormalize("https://index.docker.io/v2", out var baseUri).ShouldBeTrue();

        var probeUri = ExternalFeedProbeUri.EnsureEndsWithPathSegment(baseUri, "v2");

        probeUri.ShouldBe(new Uri("https://index.docker.io/v2/"));
    }

    [Fact]
    public void TryNormalize_ShouldKeepFileLikePathWithoutTrailingSlash()
    {
        ExternalFeedProbeUri.TryNormalize("https://helm.example.com/index.yaml", out var normalized).ShouldBeTrue();

        normalized.ShouldBe(new Uri("https://helm.example.com/index.yaml"));
    }

    private ExternalFeedConnectionTestService CreateSut()
    {
        return new ExternalFeedConnectionTestService(
            _dataProvider.Object,
            _httpClientFactory.Object,
            ProbeRules);
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        return new HttpClient(new DelegatingStubHandler(handler));
    }

    private sealed class DelegatingStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
