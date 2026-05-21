using System.Net;
using System.Net.Http;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Core.Services.Http;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

public class NuGetPackageSearchStrategyTests
{
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();

    // ── CanHandle ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("NuGet", true)]
    [InlineData("NuGet Feed", true)]
    [InlineData("nuget", true)]
    [InlineData("NUGET", true)]
    [InlineData("Docker", false)]
    [InlineData("GitHub", false)]
    [InlineData("Helm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_ShouldMatchNuGetFeedTypesCaseInsensitively(string feedType, bool expected)
    {
        var sut = CreateSut();

        sut.CanHandle(feedType).ShouldBe(expected);
    }

    // ── V3 service-index parsing ──────────────────────────────────────────────

    [Fact]
    public void FindSearchQueryServiceUrl_ShouldReturnUrl_ForValidServiceIndex()
    {
        var indexJson = """
            {
              "version": "3.0.0",
              "resources": [
                {
                  "@id": "https://example.com/v3/registration/",
                  "@type": "RegistrationsBaseUrl/3.6.0"
                },
                {
                  "@id": "https://example.com/v3/search?q=",
                  "@type": "SearchQueryService/3.5.0"
                },
                {
                  "@id": "https://example.com/v3-flatcontainer/",
                  "@type": "PackageBaseAddress/3.0.0"
                }
              ]
            }
            """;

        var url = NuGetPackageSearchStrategy.FindSearchQueryServiceUrl(indexJson);

        url.ShouldBe("https://example.com/v3/search?q=",
            customMessage:
                "FindSearchQueryServiceUrl MUST locate the SearchQueryService resource by @type prefix and " +
                "return its @id. If null/wrong, the V3 path silently falls through to V2.");
    }

    [Fact]
    public void FindSearchQueryServiceUrl_ShouldMatchAnySearchQueryServiceVersion()
    {
        // NuGet has SearchQueryService versions 3.0.0-beta, 3.0.0-rc, 3.0.0, 3.5.0.
        // Our prefix match must accept all of them.
        foreach (var typeVersion in new[]
        {
            "SearchQueryService",
            "SearchQueryService/3.0.0-beta",
            "SearchQueryService/3.0.0-rc",
            "SearchQueryService/3.0.0",
            "SearchQueryService/3.5.0",
        })
        {
            var indexJson = $$"""
                {
                  "version": "3.0.0",
                  "resources": [
                    { "@id": "https://example.com/search", "@type": "{{typeVersion}}" }
                  ]
                }
                """;

            NuGetPackageSearchStrategy.FindSearchQueryServiceUrl(indexJson)
                .ShouldBe("https://example.com/search",
                    customMessage: $"@type='{typeVersion}' MUST be recognized as a search service (NuGet has multiple version suffixes for the same resource type).");
        }
    }

    [Fact]
    public void FindSearchQueryServiceUrl_ShouldReturnNull_WhenNoSearchService()
    {
        var indexJson = """
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "https://example.com/registration", "@type": "RegistrationsBaseUrl/3.6.0" }
              ]
            }
            """;

        NuGetPackageSearchStrategy.FindSearchQueryServiceUrl(indexJson).ShouldBeNull(
            customMessage: "When index has no SearchQueryService resource, V3 path MUST fall through (return null) so V2 fallback can try.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "resources": "not an array" }""")]
    [InlineData("""{ "no_resources_key": true }""")]
    public void FindSearchQueryServiceUrl_ShouldReturnNull_ForMalformedOrIncompleteIndex(string indexJson)
    {
        NuGetPackageSearchStrategy.FindSearchQueryServiceUrl(indexJson).ShouldBeNull();
    }

    // ── V3 endpoint construction ──────────────────────────────────────────────

    [Fact]
    public void BuildV3SearchEndpoint_ShouldAppendQueryParameters()
    {
        var endpoint = NuGetPackageSearchStrategy.BuildV3SearchEndpoint("https://example.com/search", "Newtonsoft", 25);

        endpoint.ShouldContain("q=Newtonsoft");
        endpoint.ShouldContain("take=25");
        endpoint.ShouldContain("prerelease=true",
            customMessage: "prerelease=true is REQUIRED — without it, feeds with only prerelease packages silently return zero results.");
        endpoint.ShouldContain("semVerLevel=2.0.0",
            customMessage: "semVerLevel=2.0.0 is REQUIRED — without it, SemVer 2.0 packages (e.g. 1.0.0-beta.1+build.2) are silently filtered out.");
    }

    [Fact]
    public void BuildV3SearchEndpoint_ShouldUseAmpersand_WhenSearchUrlAlreadyContainsQuery()
    {
        // NuGet.org publishes its SearchQueryService URL with a trailing "?q=" baked in.
        // We must use & not ? as the separator for our extra parameters.
        var endpoint = NuGetPackageSearchStrategy.BuildV3SearchEndpoint("https://example.com/search?q=", "test", 20);

        // Verify there's exactly one '?' (no double-query-marker like ?q=?...).
        // Manual count loop — `string.Count(predicate)` resolves to the wrong
        // MemoryExtensions.Count<T>(Span<T>, T) overload via implicit conversion,
        // and System.Linq isn't in the test project's GlobalUsings.
        var questionMarkCount = 0;
        foreach (var c in endpoint) if (c == '?') questionMarkCount++;

        questionMarkCount.ShouldBe(1,
            customMessage: $"Endpoint MUST have exactly one '?'. Got: {endpoint}");
    }

    [Fact]
    public void BuildV3SearchEndpoint_ShouldUrlEncodeQuery()
    {
        var endpoint = NuGetPackageSearchStrategy.BuildV3SearchEndpoint("https://example.com/search", "foo bar+baz/qux", 10);

        endpoint.ShouldContain("q=foo%20bar%2Bbaz%2Fqux",
            customMessage: "Query MUST be URL-encoded; spaces, +, / would otherwise break the URL.");
    }

    [Fact]
    public void BuildV3SearchEndpoint_ShouldHandleEmptyOrNullQuery()
    {
        NuGetPackageSearchStrategy.BuildV3SearchEndpoint("https://example.com/search", "", 10)
            .ShouldContain("q=&");
        NuGetPackageSearchStrategy.BuildV3SearchEndpoint("https://example.com/search", null, 10)
            .ShouldContain("q=&");
    }

    // ── V3 response parsing ───────────────────────────────────────────────────

    [Fact]
    public void ParseV3SearchResponse_ShouldReturnPackageIds()
    {
        var json = """
            {
              "totalHits": 3,
              "data": [
                { "id": "Newtonsoft.Json", "version": "13.0.3" },
                { "id": "AutoMapper", "version": "12.0.1" },
                { "id": "FluentValidation", "version": "11.7.0" }
              ]
            }
            """;

        var ids = NuGetPackageSearchStrategy.ParseV3SearchResponse(json);

        ids.ShouldBe(new[] { "Newtonsoft.Json", "AutoMapper", "FluentValidation" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "data": "not an array" }""")]
    [InlineData("""{ "no_data_key": true }""")]
    public void ParseV3SearchResponse_ShouldReturnEmpty_ForMalformedOrIncompleteJson(string json)
    {
        NuGetPackageSearchStrategy.ParseV3SearchResponse(json).ShouldBeEmpty();
    }

    [Fact]
    public void ParseV3SearchResponse_ShouldSkipEntriesWithoutIdField()
    {
        var json = """
            {
              "data": [
                { "id": "ValidPackage", "version": "1.0.0" },
                { "no_id_field": true },
                { "id": "", "version": "1.0.0" }
              ]
            }
            """;

        var ids = NuGetPackageSearchStrategy.ParseV3SearchResponse(json);

        ids.ShouldBe(new[] { "ValidPackage" });
    }

    // ── V2 URL construction ───────────────────────────────────────────────────

    [Fact]
    public void BuildV2SearchUrl_ShouldEscapeSingleQuotesByDoublingThem()
    {
        // OData literal-string escape: "Bob's package" → 'Bob''s package'
        var url = NuGetPackageSearchStrategy.BuildV2SearchUrl("https://example.com/nuget", "Bob's", 10);

        // The escaped query is then URL-encoded; %27 is the encoded single quote
        url.ShouldContain("Bob%27%27s",
            customMessage:
                "Single quote inside OData string literal MUST be doubled. " +
                $"If a quote isn't escaped, the OData expression breaks. URL: {url}");
    }

    [Fact]
    public void BuildV2SearchUrl_ShouldAlwaysIncludeRequiredParameters()
    {
        var url = NuGetPackageSearchStrategy.BuildV2SearchUrl("https://example.com/nuget", "test", 20);

        url.ShouldContain("$top=20");
        url.ShouldContain("includePrerelease=true",
            customMessage: "V2 includePrerelease=true is required for symmetric coverage with V3 prerelease=true.");
        url.ShouldContain("semVerLevel=2.0.0",
            customMessage: "V2 semVerLevel=2.0.0 mirrors V3 — ignored by older servers, honoured by NuGet.Server >= 3.4.");
        url.ShouldContain("/Search()");
    }

    [Fact]
    public void BuildV2SearchUrl_ShouldStripTrailingSlash()
    {
        var url = NuGetPackageSearchStrategy.BuildV2SearchUrl("https://example.com/nuget/", "test", 10);

        url.ShouldStartWith("https://example.com/nuget/Search()");
        url.ShouldNotContain("/nuget//Search()",
            customMessage: "Trailing slash on feedUri must be stripped to avoid double-slash.");
    }

    // ── V2 response parsing ───────────────────────────────────────────────────

    [Fact]
    public void ParseV2SearchResponse_ShouldExtractIdsFromOdataXml()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry>
                <title type="text">AuthorizeNet.NetStandard</title>
                <m:properties>
                  <d:Id>AuthorizeNet.NetStandard</d:Id>
                  <d:Version>0.1.0</d:Version>
                </m:properties>
              </entry>
              <entry>
                <m:properties>
                  <d:Id>OtherPackage</d:Id>
                  <d:Version>1.0.0</d:Version>
                </m:properties>
              </entry>
            </feed>
            """;

        var ids = NuGetPackageSearchStrategy.ParseV2SearchResponse(xml, 20);

        ids.ShouldBe(new[] { "AuthorizeNet.NetStandard", "OtherPackage" });
    }

    [Fact]
    public void ParseV2SearchResponse_ShouldDedupeMultipleEntriesForSamePackage()
    {
        // V2 /Search() may return one entry per VERSION of each package. The strategy
        // must dedupe to return each package ID exactly once (the search dropdown shows
        // package IDs, not (id, version) pairs).
        var xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><m:properties><d:Id>PackageA</d:Id><d:Version>1.0.0</d:Version></m:properties></entry>
              <entry><m:properties><d:Id>PackageA</d:Id><d:Version>2.0.0</d:Version></m:properties></entry>
              <entry><m:properties><d:Id>PackageB</d:Id><d:Version>1.0.0</d:Version></m:properties></entry>
              <entry><m:properties><d:Id>packagea</d:Id><d:Version>3.0.0</d:Version></m:properties></entry>
            </feed>
            """;

        var ids = NuGetPackageSearchStrategy.ParseV2SearchResponse(xml, 20);

        ids.Count.ShouldBe(2,
            customMessage:
                "Three entries for PackageA (one with different casing) MUST dedupe to one. " +
                "Otherwise the UI shows duplicate options.");
        ids.ShouldContain("PackageA");
        ids.ShouldContain("PackageB");
    }

    [Fact]
    public void ParseV2SearchResponse_ShouldRespectTakeLimit()
    {
        var xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><m:properties><d:Id>A</d:Id></m:properties></entry>
              <entry><m:properties><d:Id>B</d:Id></m:properties></entry>
              <entry><m:properties><d:Id>C</d:Id></m:properties></entry>
              <entry><m:properties><d:Id>D</d:Id></m:properties></entry>
            </feed>
            """;

        var ids = NuGetPackageSearchStrategy.ParseV2SearchResponse(xml, 2);

        ids.Count.ShouldBe(2);
        ids.ShouldBe(new[] { "A", "B" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<feed></feed>")]
    public void ParseV2SearchResponse_ShouldReturnEmpty_ForMalformedOrEmptyXml(string xml)
    {
        NuGetPackageSearchStrategy.ParseV2SearchResponse(xml, 20).ShouldBeEmpty();
    }

    // ── Full SearchAsync — V3 happy path ──────────────────────────────────────

    [Fact]
    public async Task SearchAsync_V3HappyPath_ReturnsParsedIds()
    {
        var indexJson = """
            { "resources": [{ "@id": "https://example.com/search", "@type": "SearchQueryService/3.5.0" }] }
            """;
        var searchJson = """
            { "data": [{ "id": "Newtonsoft.Json" }, { "id": "FluentAssertions" }] }
            """;

        var client = CreateHttpClient(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/index.json")) return Ok(indexJson);
            if (url.StartsWith("https://example.com/search")) return Ok(searchJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/v3" };

        var result = await sut.SearchAsync(feed, "newton", 20, CancellationToken.None);

        result.ShouldBe(new[] { "Newtonsoft.Json", "FluentAssertions" });
    }

    // ── Full SearchAsync — V2 fallback when V3 service-index is missing ───────

    [Fact]
    public async Task SearchAsync_V2Fallback_WhenV3IndexReturns404()
    {
        var v2Xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><m:properties><d:Id>LegacyPackage</d:Id></m:properties></entry>
            </feed>
            """;

        var client = CreateHttpClient(request =>
        {
            var url = request.RequestUri!.ToString();

            // V3 service index returns 404 → V3 attempt aborts, V2 fallback kicks in
            if (url.EndsWith("/index.json")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (url.Contains("/Search()")) return Ok(v2Xml);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/nuget" };

        var result = await sut.SearchAsync(feed, "", 20, CancellationToken.None);

        result.ShouldBe(
            new[] { "LegacyPackage" },
            ignoreOrder: false,
            customMessage:
                "When V3 /index.json returns 404, the strategy MUST fall back to V2 OData /Search(). " +
                "This is the only path for legacy NuGet.Server installations.");
    }

    [Fact]
    public async Task SearchAsync_BothProtocolsFail_ReturnsEmpty()
    {
        var client = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://example.com/nuget" };

        var result = await sut.SearchAsync(feed, "test", 20, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_EmptyFeedUri_ReturnsEmptyWithoutHttpCall()
    {
        var httpCallMade = false;
        var client = CreateHttpClient(_ =>
        {
            httpCallMade = true;
            return Ok("[]");
        });

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "" };

        var result = await sut.SearchAsync(feed, "test", 20, CancellationToken.None);

        result.ShouldBeEmpty();
        httpCallMade.ShouldBeFalse(
            customMessage: "Empty FeedUri MUST short-circuit before any HTTP call to avoid wasted network round-trips.");
    }

    // ── Edge cases — auth failure, timeout, special chars, pagination ────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]    // wrong credentials
    [InlineData(HttpStatusCode.Forbidden)]       // valid creds but not allowed to read
    public async Task SearchAsync_AuthFailureOnV3Index_FallsBackToV2_AlsoFails_ReturnsEmpty(HttpStatusCode authError)
    {
        // V3 service index returns 401/403 → strategy aborts V3 attempt + tries V2.
        // V2 /Search() also returns the same auth error → empty result (no exception).
        // The strategy MUST treat auth failure as "no results", not crash — otherwise
        // a misconfigured private feed crashes every deploy that searches it.
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

        var result = await sut.SearchAsync(feed, "Newtonsoft", 20, CancellationToken.None);

        result.ShouldBeEmpty(
            customMessage:
                $"Auth failure ({authError}) MUST be swallowed by the strategy as 'no results'. " +
                "Crashing here would block every deploy whose pipeline brushes this feed.");
    }

    [Fact]
    public async Task SearchAsync_HttpClientThrows_TaskCanceled_ReturnsEmpty()
    {
        // Simulates network timeout / cancellation. The strategy's inner try/catch
        // MUST absorb the exception and return empty.
        var client = CreateHttpClient(_ => throw new TaskCanceledException("simulated timeout"));

        _httpClientFactory.Setup(x => x.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(client);

        var sut = CreateSut();
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = "https://slow.example.com/nuget" };

        var result = await sut.SearchAsync(feed, "test", 20, CancellationToken.None);

        result.ShouldBeEmpty(
            customMessage:
                "Network exceptions in the search path MUST be swallowed — the strategy's " +
                "try/catch returns empty so an unreachable feed doesn't crash the entire UI's package picker.");
    }

    [Theory]
    [InlineData("foo bar", "foo%20bar")]
    [InlineData("foo+bar", "foo%2Bbar")]
    [InlineData("foo/bar", "foo%2Fbar")]
    [InlineData("foo&bar", "foo%26bar")]
    [InlineData("foo#bar", "foo%23bar")]
    [InlineData("foo=bar", "foo%3Dbar")]
    public void BuildV3SearchEndpoint_SpecialCharsInQuery_AreUrlEncoded(string raw, string expectedEncoded)
    {
        // Defense-in-depth: special chars in package query MUST be URL-encoded
        // before going into the V3 ?q= parameter. Without encoding, '&' / '=' /
        // '/' break the URL structure and the feed returns garbage.
        var endpoint = NuGetPackageSearchStrategy.BuildV3SearchEndpoint("https://example.com/search", raw, 20);

        endpoint.ShouldContain($"q={expectedEncoded}",
            customMessage:
                $"Query '{raw}' MUST encode to '{expectedEncoded}'. Full endpoint: {endpoint}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private NuGetPackageSearchStrategy CreateSut() => new(_httpClientFactory.Object);

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
