using System.Net.Http;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Http;
using Xunit;

namespace Squid.E2ETests.Deployments.ExternalFeeds;

/// <summary>
/// Live E2E tests driving <see cref="NuGetPackageSearchStrategy"/> and
/// <see cref="NuGetPackageVersionStrategy"/> against a real operator NuGet feed
/// (<c>https://nuget.sjfood.us/nuget</c>) — the same feed bound in the SquidWeb
/// frontend during the IIS-deploy smoke validation that surfaced the original
/// "search returns nothing" bug. Provides end-to-end confidence that:
///
/// <list type="number">
/// <item><b>The V2 fallback actually works</b> against a V2-only NuGet.Server
///       installation. Operator NuGet.Server (private mirrors, on-prem feeds)
///       commonly serve V2 only — V3 service index returns 404. The unit suite
///       exercises both protocols with mocked HTTP, but only a live call against
///       a real V2-only server proves the fallback path is correct.</item>
/// <item><b>The OData XML parser handles real-world responses</b> with their
///       namespace prefixes, optional fields, mixed-case casing, and inline
///       pagination links exactly as the OData spec describes.</item>
/// <item><b>The chained search → version flow works</b> — pick a package via
///       search, then list its versions. This is the exact UX path an operator
///       drives from the IIS-deploy step UI's Package selector.</item>
/// </list>
///
/// <para><b>Tier (per Rule 12)</b>: 🟢 High-fidelity for the strategy code under
/// test (real production class + real HTTP); 🟡 Medium-fidelity for the broader
/// service composition since we don't drive the request through
/// <c>ExternalFeedPackageSearchService</c> + DI — the tests instantiate the
/// strategies directly with a thin <c>ISquidHttpClientFactory</c> wrapper that
/// returns plain <c>HttpClient</c>. The service layer is already covered by
/// <c>ExternalFeedPackageSearchServiceTests</c> (unit tier).</para>
///
/// <para><b>Network skip semantics</b>: every <c>[Fact]</c> probes connectivity
/// to the feed root in a 5s timeout before running the actual assertion. If the
/// feed is unreachable (operator's mirror down, CI runner without egress), the
/// test returns CLEANLY without failing. This is the same skip-on-disconnect
/// philosophy as the Kind cluster fixture in <c>Squid.E2ETests</c>.</para>
///
/// <para><b>Assertion style — resilient, not brittle</b>: assertions check
/// "non-empty result" + "chained flow works" rather than pinning specific
/// package IDs / versions. The feed's contents can drift; tests must not. The
/// only fixed assertion is on the search-then-version round-trip
/// (any → any), proving the protocol contract end-to-end.</para>
/// </summary>
[Trait("Category", "LiveNuGetFeed")]
public class NuGetFeedLiveE2ETests
{
    private const string LiveFeedUri = "https://nuget.sjfood.us/nuget";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsAtLeastOnePackage_AgainstLiveV2Feed()
    {
        if (!await IsFeedReachableAsync().ConfigureAwait(false)) return;

        var strategy = new NuGetPackageSearchStrategy(new LiveHttpClientFactory());
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = LiveFeedUri };

        var packages = await strategy.SearchAsync(feed, "", 20, CancellationToken.None).ConfigureAwait(false);

        packages.ShouldNotBeNull();
        packages.Count.ShouldBeGreaterThan(0,
            customMessage:
                $"Search with empty query against {LiveFeedUri} MUST return at least one package. " +
                $"If empty, either the V2 fallback isn't engaging on a V2-only feed, or the OData XML " +
                $"parser isn't extracting <d:Id> elements. Manual diagnose: " +
                $"`curl '{LiveFeedUri}/Search()?$top=3&searchTerm='''''&includePrerelease=true' | xmllint --format -` " +
                $"should show <entry> elements with <d:Id> children.");
    }

    [Fact]
    public async Task SearchAsync_DedupsPackageIdsAcrossVersions_AgainstLiveV2Feed()
    {
        // V2 /Search() returns one entry per (id, version) pair, so a package with N
        // versions yields N entries. The parser MUST dedupe so the UI dropdown shows
        // each package ID exactly once. Verifies on the LIVE feed because this is the
        // single most-likely silent failure (parser regression: results show as
        // PackageA, PackageA, PackageA, ... instead of unique IDs).
        if (!await IsFeedReachableAsync().ConfigureAwait(false)) return;

        var strategy = new NuGetPackageSearchStrategy(new LiveHttpClientFactory());
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = LiveFeedUri };

        var packages = await strategy.SearchAsync(feed, "", 50, CancellationToken.None).ConfigureAwait(false);

        var distinctCount = packages.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        distinctCount.ShouldBe(packages.Count,
            customMessage:
                $"Search results MUST be unique by package ID. Got {packages.Count} results but only " +
                $"{distinctCount} distinct IDs — the V2 OData parser is not deduping. The UI would show " +
                $"duplicate options in the package dropdown. Suspect: ParseV2SearchResponse's HashSet logic.");
    }

    [Fact]
    public async Task ChainedSearchAndListVersions_OnLiveFeed_ReturnsAtLeastOneVersionForFirstHit()
    {
        // The full UX path: operator selects a NuGet feed → searches → picks a package
        // → version dropdown populates. This test exercises the chain end-to-end.
        // Resilient: doesn't assume specific package contents; picks whatever the first
        // search hit is and asserts that THAT package has at least one version.
        if (!await IsFeedReachableAsync().ConfigureAwait(false)) return;

        var searchStrategy = new NuGetPackageSearchStrategy(new LiveHttpClientFactory());
        var versionStrategy = new NuGetPackageVersionStrategy(new LiveHttpClientFactory());
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = LiveFeedUri };

        var packages = await searchStrategy.SearchAsync(feed, "", 5, CancellationToken.None).ConfigureAwait(false);

        packages.Count.ShouldBeGreaterThan(0,
            customMessage:
                "Chained flow pre-condition: search MUST return at least one package. " +
                "If 0, the chained-flow test cannot verify version listing.");

        var firstPackageId = packages[0];
        var versions = await versionStrategy.ListVersionsAsync(feed, firstPackageId, CancellationToken.None).ConfigureAwait(false);

        versions.ShouldNotBeNull();
        versions.Count.ShouldBeGreaterThan(0,
            customMessage:
                $"After search returned '{firstPackageId}', ListVersionsAsync against the same feed " +
                $"MUST return at least one version. If 0, the version strategy's V2 fallback isn't " +
                $"engaging or the OData XML parser isn't extracting <d:Version>/<d:NormalizedVersion>. " +
                $"Manual diagnose: " +
                $"`curl \"{LiveFeedUri}/FindPackagesById()?id='{firstPackageId}'&semVerLevel=2.0.0\" | xmllint --format -` " +
                $"should show <entry> elements with version data.");
    }

    [Fact]
    public async Task ListVersionsAsync_CaseInsensitivePackageId_AgainstLiveV2Feed()
    {
        // NuGet package IDs are documented case-insensitive. The V3 flat-container
        // path is case-sensitive (we lowercase before lookup); V2 OData is
        // case-insensitive natively. This test catches a regression where the
        // strategy accidentally case-folds the operator's input in a way that
        // breaks V2 lookups (e.g. lowercasing for V3 then passing the lowercased
        // form into the V2 fallback URL — which would break some V2 servers that
        // do case-sensitive comparison despite the spec).
        if (!await IsFeedReachableAsync().ConfigureAwait(false)) return;

        var searchStrategy = new NuGetPackageSearchStrategy(new LiveHttpClientFactory());
        var versionStrategy = new NuGetPackageVersionStrategy(new LiveHttpClientFactory());
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = LiveFeedUri };

        var packages = await searchStrategy.SearchAsync(feed, "", 1, CancellationToken.None).ConfigureAwait(false);

        if (packages.Count == 0) return;    // Feed is empty — skip the case-insensitive sub-check

        var operatorCasing = packages[0];
        var uppercased = operatorCasing.ToUpperInvariant();
        var lowercased = operatorCasing.ToLowerInvariant();

        var versionsOperator = await versionStrategy.ListVersionsAsync(feed, operatorCasing, CancellationToken.None).ConfigureAwait(false);
        var versionsUpper = await versionStrategy.ListVersionsAsync(feed, uppercased, CancellationToken.None).ConfigureAwait(false);
        var versionsLower = await versionStrategy.ListVersionsAsync(feed, lowercased, CancellationToken.None).ConfigureAwait(false);

        versionsOperator.Count.ShouldBeGreaterThan(0,
            customMessage:
                $"Baseline: ListVersionsAsync('{operatorCasing}') MUST find versions. " +
                "If 0, the chained flow itself is broken — not a casing issue.");

        // The other two cases SHOULD produce the same count IF the feed implements
        // case-insensitive lookups (NuGet.Server / BaGet / NuGet.org all do). A
        // non-compliant feed wouldn't, in which case we accept "either equal OR 0"
        // — we only fail when the count is NEITHER zero NOR equal (which would
        // indicate a partial-match bug specific to our strategy).
        AssertCaseInsensitiveCountMatches(versionsOperator.Count, versionsUpper.Count, $"{operatorCasing} vs {uppercased}");
        AssertCaseInsensitiveCountMatches(versionsOperator.Count, versionsLower.Count, $"{operatorCasing} vs {lowercased}");
    }

    private static void AssertCaseInsensitiveCountMatches(int expected, int actual, string casePair)
    {
        var acceptable = actual == expected || actual == 0;

        acceptable.ShouldBeTrue(
            customMessage:
                $"Case-insensitive lookup mismatch ({casePair}): expected {expected} versions, " +
                $"got {actual}. Either a server-side case-sensitive comparison (acceptable, will be 0) " +
                $"OR a partial parsing bug in our strategy (NOT acceptable). If the live feed lookup " +
                $"returns a non-zero, non-matching count, the strategy is finding SOMETHING but not " +
                $"the full set — investigate URL encoding or partial-match parsing.");
    }

    [Fact]
    public async Task SearchAsync_WithBogusQueryNotInFeed_ReturnsEmptyOrFewResults_AgainstLiveV2Feed()
    {
        // Behavioural sanity check: a query that's extremely unlikely to match
        // anything must return a small / empty result set, NOT a server error.
        // If a parser regression makes us misinterpret "no results" as "all results"
        // (e.g. by ignoring the search term), this would catch it.
        if (!await IsFeedReachableAsync().ConfigureAwait(false)) return;

        var strategy = new NuGetPackageSearchStrategy(new LiveHttpClientFactory());
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = LiveFeedUri };

        var packages = await strategy.SearchAsync(feed, "qqq-bogus-no-package-with-this-name-zzz-9999", 20, CancellationToken.None).ConfigureAwait(false);

        // Either empty (most likely) or very few — never 20+ since the searchTerm
        // is enforced by the server. If 20+, something's wrong with the URL
        // construction (searchTerm not actually being applied).
        packages.Count.ShouldBeLessThan(20,
            customMessage:
                $"Bogus-query search MUST be filtered by the server. Got {packages.Count} results — " +
                $"if 20 (the take limit), the searchTerm parameter isn't being honoured. Either the " +
                $"OData URL is malformed (missing quoting / escaping) or the server is ignoring the param.");
    }

    [Fact]
    public async Task GetNotesAsync_FetchesV2AtomEntry_WithoutFormatJsonQueryParam_AgainstLiveV2Feed()
    {
        // Regression pin for "Nuget feed returned 400" during release creation.
        // Root cause was the V2 fallback in NuGetPackageNotesStrategy requesting
        // ?$format=json — which the default NuGet.Server configuration REJECTS
        // (returns 400/404 with OData error "Query option 'Format' is not
        // allowed"). The fix removes the format param so the server returns the
        // default Atom XML; the parser then extracts <d:ReleaseNotes> /
        // <d:Description> / <d:Published> via XDocument.
        //
        // This test runs against the operator's actual NuGet.Server installation
        // — if the strategy ever regresses to ?$format=json, sjfood's response
        // changes from 200+Atom to 400+OData-error and this test fails loudly.
        if (!await IsFeedReachableAsync().ConfigureAwait(false)) return;

        var notesStrategy = new NuGetPackageNotesStrategy(new LiveHttpClientFactory());
        var searchStrategy = new NuGetPackageSearchStrategy(new LiveHttpClientFactory());
        var versionStrategy = new NuGetPackageVersionStrategy(new LiveHttpClientFactory());
        var feed = new ExternalFeed { FeedType = "NuGet", FeedUri = LiveFeedUri };

        // Discover a real package + version pair from the live feed — resilient
        // to feed contents drift (don't hardcode "AuthorizeNet.NetStandard").
        var packages = await searchStrategy.SearchAsync(feed, "", 1, CancellationToken.None).ConfigureAwait(false);
        if (packages.Count == 0) return;

        var firstPackageId = packages[0];
        var versions = await versionStrategy.ListVersionsAsync(feed, firstPackageId, CancellationToken.None).ConfigureAwait(false);
        if (versions.Count == 0) return;

        var firstVersion = versions[0];

        var result = await notesStrategy.GetNotesAsync(feed, firstPackageId, firstVersion, CancellationToken.None).ConfigureAwait(false);

        result.ShouldNotBeNull();
        result.Succeeded.ShouldBeTrue(
            customMessage:
                $"GetNotesAsync against the live V2 feed MUST succeed (not Failure). " +
                $"If FailureReason starts with 'NuGet feed returned 400', the strategy regressed to using " +
                $"?$format=json which NuGet.Server's EnableQueryAttribute rejects. " +
                $"Tested package: '{firstPackageId}' v{firstVersion}. " +
                $"FailureReason: '{result.FailureReason}'.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<bool> IsFeedReachableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = ProbeTimeout };
            using var response = await client.GetAsync($"{LiveFeedUri}/$metadata").ConfigureAwait(false);

            // Any 2xx OR 4xx response means the server is up and responding (4xx is fine —
            // it means we reached the host but the path was wrong). 5xx or transport
            // failure = server unreachable / down — skip the test.
            return (int)response.StatusCode < 500;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    /// <summary>
    /// Minimal <see cref="ISquidHttpClientFactory"/> that returns a real
    /// <see cref="HttpClient"/>. The strategies only call <see cref="ISquidHttpClientFactory.CreateClient"/>;
    /// the other methods throw so any future regression that uses them is caught.
    /// </summary>
    private sealed class LiveHttpClientFactory : ISquidHttpClientFactory
    {
        public HttpClient CreateClient(TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null)
        {
            var client = new HttpClient();

            if (timeout.HasValue) client.Timeout = timeout.Value;

            if (headers != null)
            {
                foreach (var h in headers)
                    client.DefaultRequestHeaders.Add(h.Key, h.Value);
            }

            return client;
        }

        public Task<T> GetAsync<T>(string requestUrl, CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false) => throw new NotImplementedException();
        public Task<T> PostAsync<T>(string requestUrl, HttpContent content, CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false) => throw new NotImplementedException();
        public Task<T> PutAsync<T>(string requestUrl, HttpContent content = null, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<T> DeleteAsync<T>(string requestUrl, HttpContent content = null, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<T> PostAsJsonAsync<T>(string requestUrl, object value, CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false) => throw new NotImplementedException();
        public Task<HttpResponseMessage> PostAsJsonAsync(string requestUrl, object value, CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true) => throw new NotImplementedException();
        public Task<T> PostAsStreamAsync<T>(string requestUrl, object value, CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false) => throw new NotImplementedException();
        public Task<T> PostAsMultipartAsync<T>(string requestUrl, Dictionary<string, string> formData, Dictionary<string, (byte[], string)> fileData, CancellationToken cancellationToken, TimeSpan? timeout = null, bool beginScope = false, Dictionary<string, string> headers = null, bool shouldLogError = true, bool isNeedToReadErrorContent = false) => throw new NotImplementedException();
    }
}
