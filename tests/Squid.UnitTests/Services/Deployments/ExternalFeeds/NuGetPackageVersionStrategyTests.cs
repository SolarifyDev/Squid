using System.Net;
using System.Net.Http;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class NuGetPackageVersionStrategyTests
{
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();

    [Theory]
    [InlineData("NuGet", true)]
    [InlineData("nuget", true)]
    [InlineData("NUGET", true)]
    [InlineData("NuGet Feed", true)]
    [InlineData("Docker", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_ShouldMatchNuGetFeedTypesCaseInsensitively(string feedType, bool expected)
    {
        var sut = CreateSut();

        sut.CanHandle(feedType).ShouldBe(expected);
    }

    // ── V3 service-index parsing ──────────────────────────────────────────────

    [Fact]
    public void FindPackageBaseAddressUrl_ShouldReturnUrl_ForValidServiceIndex()
    {
        var indexJson = """
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "https://example.com/search", "@type": "SearchQueryService/3.5.0" },
                { "@id": "https://example.com/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0" }
              ]
            }
            """;

        NuGetPackageVersionStrategy.FindPackageBaseAddressUrl(indexJson)
            .ShouldBe("https://example.com/v3-flatcontainer/");
    }

    [Fact]
    public void FindPackageBaseAddressUrl_ShouldReturnNull_WhenNoFlatContainerResource()
    {
        var indexJson = """
            {
              "resources": [
                { "@id": "https://example.com/search", "@type": "SearchQueryService/3.5.0" }
              ]
            }
            """;

        NuGetPackageVersionStrategy.FindPackageBaseAddressUrl(indexJson).ShouldBeNull(
            customMessage: "When PackageBaseAddress is absent, V3 path MUST return null so V2 fallback can try.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void FindPackageBaseAddressUrl_ShouldReturnNull_ForMalformedJson(string indexJson)
    {
        NuGetPackageVersionStrategy.FindPackageBaseAddressUrl(indexJson).ShouldBeNull();
    }

    // ── V3 versions response parsing ──────────────────────────────────────────

    [Fact]
    public void ParseV3VersionsResponse_ShouldReturnVersionsArray()
    {
        var json = """
            {
              "versions": ["1.0.0", "1.1.0", "2.0.0-beta", "2.0.0-rc.1+build.5", "2.0.0"]
            }
            """;

        var versions = NuGetPackageVersionStrategy.ParseV3VersionsResponse(json, 100);

        versions.ShouldBe(new[] { "1.0.0", "1.1.0", "2.0.0-beta", "2.0.0-rc.1+build.5", "2.0.0" });
    }

    [Fact]
    public void ParseV3VersionsResponse_ShouldRespectEnumerationCap()
    {
        var json = """
            { "versions": ["1.0.0", "2.0.0", "3.0.0", "4.0.0", "5.0.0"] }
            """;

        var versions = NuGetPackageVersionStrategy.ParseV3VersionsResponse(json, 3);

        versions.Count.ShouldBe(3,
            customMessage: "Enumeration cap MUST be enforced at the strategy level to prevent OOM on pathological feeds.");
        versions.ShouldBe(new[] { "1.0.0", "2.0.0", "3.0.0" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "versions": "not array" }""")]
    public void ParseV3VersionsResponse_ShouldReturnEmpty_ForMalformedJson(string json)
    {
        NuGetPackageVersionStrategy.ParseV3VersionsResponse(json, 100).ShouldBeEmpty();
    }

    // ── V2 URL construction ───────────────────────────────────────────────────

    [Fact]
    public void BuildV2VersionsUrl_ShouldEscapeSingleQuotesByDoublingThem()
    {
        var url = NuGetPackageVersionStrategy.BuildV2VersionsUrl("https://example.com/nuget", "Bob's.Package");

        url.ShouldContain("Bob%27%27s.Package",
            customMessage:
                "Single quote inside OData literal MUST be doubled and URL-encoded. " +
                $"Otherwise the OData expression breaks. URL: {url}");
    }

    [Fact]
    public void BuildV2VersionsUrl_ShouldIncludeSemVerLevel2()
    {
        var url = NuGetPackageVersionStrategy.BuildV2VersionsUrl("https://example.com/nuget", "Newtonsoft.Json");

        url.ShouldContain("semVerLevel=2.0.0",
            customMessage: "semVerLevel=2.0.0 ensures SemVer 2 versions (e.g. 1.0.0-beta.1+build.2) aren't dropped.");
        url.ShouldContain("/FindPackagesById()");
    }

    [Fact]
    public void BuildV2VersionsUrl_ShouldStripTrailingSlash()
    {
        var url = NuGetPackageVersionStrategy.BuildV2VersionsUrl("https://example.com/nuget/", "Newtonsoft.Json");

        url.ShouldStartWith("https://example.com/nuget/FindPackagesById()");
        url.ShouldNotContain("/nuget//FindPackagesById");
    }

    // ── V2 response parsing ───────────────────────────────────────────────────

    [Fact]
    public void ParseV2VersionsResponse_ShouldExtractVersions_PreferNormalizedVersionWhenPresent()
    {
        // Real NuGet.Server response: NormalizedVersion may differ from Version
        // (e.g. "1.0" vs "1.0.0"). Tests verify we prefer NormalizedVersion.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry>
                <m:properties>
                  <d:Id>AuthorizeNet.NetStandard</d:Id>
                  <d:Version>0.1.0</d:Version>
                  <d:NormalizedVersion>0.1.0</d:NormalizedVersion>
                </m:properties>
              </entry>
              <entry>
                <m:properties>
                  <d:Id>AuthorizeNet.NetStandard</d:Id>
                  <d:Version>0.2</d:Version>
                  <d:NormalizedVersion>0.2.0</d:NormalizedVersion>
                </m:properties>
              </entry>
            </feed>
            """;

        var (versions, nextLink) = NuGetPackageVersionStrategy.ParseV2VersionsResponse(xml);

        versions.ShouldBe(
            new[] { "0.1.0", "0.2.0" },
            ignoreOrder: false,
            customMessage:
                "Strategy MUST prefer NormalizedVersion over Version. The unnormalized '0.2' would round-trip " +
                "inconsistently with .NET's NuGetVersion parser; the normalized '0.2.0' is the canonical form.");
        nextLink.ShouldBeNull();
    }

    [Fact]
    public void ParseV2VersionsResponse_ShouldFallBackToVersion_WhenNormalizedVersionAbsent()
    {
        var xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry>
                <m:properties>
                  <d:Version>1.0.0</d:Version>
                </m:properties>
              </entry>
            </feed>
            """;

        var (versions, _) = NuGetPackageVersionStrategy.ParseV2VersionsResponse(xml);

        versions.ShouldBe(new[] { "1.0.0" });
    }

    [Fact]
    public void ParseV2VersionsResponse_ShouldExtractInlineNextLink()
    {
        var xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <link rel="self" href="https://example.com/nuget/FindPackagesById()?id='X'" />
              <link rel="next" href="https://example.com/nuget/FindPackagesById()?id='X'&amp;skip=40" />
              <entry>
                <m:properties>
                  <d:Version>1.0.0</d:Version>
                </m:properties>
              </entry>
            </feed>
            """;

        var (versions, nextLink) = NuGetPackageVersionStrategy.ParseV2VersionsResponse(xml);

        versions.ShouldBe(new[] { "1.0.0" });
        nextLink.ShouldBe("https://example.com/nuget/FindPackagesById()?id='X'&skip=40",
            customMessage: "Next-link from OData inline pagination MUST be extracted so we can iterate large version histories.");
    }

    [Fact]
    public void ParseV2VersionsResponse_ShouldReturnNullNextLink_WhenAbsent()
    {
        var xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <link rel="self" href="https://example.com/nuget/FindPackagesById()?id='X'" />
              <entry><m:properties><d:Version>1.0.0</d:Version></m:properties></entry>
            </feed>
            """;

        var (_, nextLink) = NuGetPackageVersionStrategy.ParseV2VersionsResponse(xml);

        nextLink.ShouldBeNull(
            customMessage: "When there's no <link rel='next'>, the pagination loop MUST stop. Else infinite-loop risk.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<feed></feed>")]
    public void ParseV2VersionsResponse_ShouldReturnEmpty_ForMalformedXml(string xml)
    {
        var (versions, nextLink) = NuGetPackageVersionStrategy.ParseV2VersionsResponse(xml);

        versions.ShouldBeEmpty();
        nextLink.ShouldBeNull();
    }

    // ── Full ListVersionsAsync — V3 happy path ────────────────────────────────

    [Fact]
    public async Task ListVersionsAsync_V3HappyPath_ReturnsParsedVersions()
    {
        var indexJson = """
            { "resources": [{ "@id": "https://example.com/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0" }] }
            """;
        var versionsJson = """
            { "versions": ["1.0.0", "1.1.0", "2.0.0"] }
            """;

        var v3PathLowercased = false;

        var client = CreateHttpClient(request =>
        {
            var url = request.RequestUri!.ToString();

            // The per-package versions URL ALSO ends in /index.json (it's
            // `{base}/{id-lower}/index.json`), so the order of these checks
            // matters: match the longer-suffix path FIRST.
            if (url.Contains("/v3-flatcontainer/newtonsoft.json/index.json", StringComparison.Ordinal))
            {
                v3PathLowercased = true;
                return Ok(versionsJson);
            }

            if (url.EndsWith("https://example.com/v3/index.json", StringComparison.Ordinal))
                return Ok(indexJson);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/v3" };

        // Operator types mixed-case package ID; the V3 path MUST lowercase before lookup.
        var result = await sut.ListVersionsAsync(feed, "Newtonsoft.Json", CancellationToken.None);

        result.ShouldBe(new[] { "1.0.0", "1.1.0", "2.0.0" });
        v3PathLowercased.ShouldBeTrue(
            customMessage: "V3 PackageBaseAddress URL MUST use the lowercased package ID. The flat-container endpoint is case-sensitive on the path segment.");
    }

    [Fact]
    public async Task ListVersionsAsync_V2Fallback_WhenV3IndexReturns404()
    {
        var v2Xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><m:properties><d:Version>0.1.0</d:Version></m:properties></entry>
              <entry><m:properties><d:Version>0.2.0</d:Version></m:properties></entry>
            </feed>
            """;

        var client = CreateHttpClient(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.EndsWith("/index.json")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (url.Contains("/FindPackagesById()")) return Ok(v2Xml);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/nuget" };

        var result = await sut.ListVersionsAsync(feed, "AuthorizeNet.NetStandard", CancellationToken.None);

        result.ShouldBe(new[] { "0.1.0", "0.2.0" });
    }

    [Fact]
    public async Task ListVersionsAsync_V2FollowsInlinePagination_AndDedupes()
    {
        var page1 = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <link rel="next" href="https://example.com/nuget/FindPackagesById()?id='Pkg'&amp;skip=2" />
              <entry><m:properties><d:Version>1.0.0</d:Version></m:properties></entry>
              <entry><m:properties><d:Version>2.0.0</d:Version></m:properties></entry>
            </feed>
            """;
        var page2 = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><m:properties><d:Version>3.0.0</d:Version></m:properties></entry>
              <entry><m:properties><d:Version>1.0.0</d:Version></m:properties></entry>
            </feed>
            """;

        var client = CreateHttpClient(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.EndsWith("/index.json")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (url.Contains("skip=2")) return Ok(page2);
            if (url.Contains("/FindPackagesById()")) return Ok(page1);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/nuget" };

        var result = await sut.ListVersionsAsync(feed, "Pkg", CancellationToken.None);

        result.ShouldBe(
            new[] { "1.0.0", "2.0.0", "3.0.0" },
            ignoreOrder: false,
            customMessage:
                "Pagination MUST follow inline <link rel='next'> and dedupe (1.0.0 appears on both pages). " +
                "Without dedupe, the version dropdown shows duplicates.");
    }

    [Fact]
    public async Task ListVersionsAsync_EmptyPackageId_ReturnsEmptyWithoutHttpCall()
    {
        var httpCallMade = false;
        var client = CreateHttpClient(_ =>
        {
            httpCallMade = true;
            return Ok("{}");
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/nuget" };

        var result = await sut.ListVersionsAsync(feed, "", CancellationToken.None);

        result.ShouldBeEmpty();
        httpCallMade.ShouldBeFalse();
    }

    [Fact]
    public async Task ListVersionsAsync_BothProtocolsFail_ReturnsEmpty()
    {
        var client = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/nuget" };

        var result = await sut.ListVersionsAsync(feed, "AnyPackage", CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // ── Edge cases — auth failure, timeout, pagination loop ──────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ListVersionsAsync_AuthFailure_ReturnsEmpty(HttpStatusCode authError)
    {
        var client = CreateHttpClient(_ => new HttpResponseMessage(authError));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed
        {
            FeedType = "NuGet",
            FeedUri = "https://example.com/nuget",
            Username = "wrong",
            Password = "creds"
        };

        var result = await sut.ListVersionsAsync(feed, "AnyPackage", CancellationToken.None);

        result.ShouldBeEmpty(
            customMessage:
                $"Auth failure ({authError}) MUST return empty (no exception). Otherwise a misconfigured " +
                "private feed crashes every release-creation that brushes it.");
    }

    [Fact]
    public async Task ListVersionsAsync_HttpClientThrows_TaskCanceled_ReturnsEmpty()
    {
        var client = CreateHttpClient(_ => throw new TaskCanceledException("simulated timeout"));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://slow.example.com/nuget" };

        var result = await sut.ListVersionsAsync(feed, "AnyPackage", CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListVersionsAsync_V2PaginationLoop_TerminatesOnEmptyNextLink()
    {
        // Pagination loop safety — must terminate when no <link rel="next">.
        // Failure mode: infinite loop hanging the UI's version-listing call.
        // Two pages of versions, second has no next link.
        var page1 = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <link rel="next" href="https://example.com/nuget/FindPackagesById()?id='X'&amp;%24skip=1" />
              <entry><m:properties><d:Version>1.0.0</d:Version></m:properties></entry>
            </feed>
            """;
        var page2 = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><m:properties><d:Version>2.0.0</d:Version></m:properties></entry>
            </feed>
            """;

        var hits = 0;
        var client = CreateHttpClient(req =>
        {
            hits++;
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/index.json")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (url.Contains("%24skip=1")) return Ok(page2);
            if (url.Contains("/FindPackagesById()")) return Ok(page1);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/nuget" };

        var result = await sut.ListVersionsAsync(feed, "X", CancellationToken.None);

        result.ShouldBe(new[] { "1.0.0", "2.0.0" });

        hits.ShouldBeLessThan(10,
            customMessage:
                $"V2 pagination loop made {hits} HTTP requests for a 2-page result. " +
                "Should be at most 3 (V3 index probe + 2 pages). Anything higher means the loop " +
                "isn't terminating on missing next-link — fix immediately or production will hang.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private NuGetPackageVersionStrategy CreateSut() => new(_httpClientFactory.Object);

    private static HttpResponseMessage Ok(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new StubHandler(handler));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }
}
