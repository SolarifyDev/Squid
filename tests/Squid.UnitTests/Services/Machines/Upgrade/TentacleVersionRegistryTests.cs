using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using Moq.Protected;
using Squid.Core.DependencyInjection;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Http;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Coverage for the live Tentacle-version source-of-truth. Single
/// responsibility: "given a CommunicationStyle, what is the latest
/// published version". URL building / artefact delivery deliberately lives
/// elsewhere (per-strategy) — see the SOLID note on
/// <see cref="ITentacleVersionRegistry"/>.
///
/// <para>Live Docker Hub queries are NOT exercised here — those need the
/// network and would be flaky in CI; integration tests cover the live
/// path. The HTTP boundary is short-circuited by env-var overrides for
/// every test in this file, proving the override path runs before any IO.</para>
/// </summary>
public sealed class TentacleVersionRegistryTests : IDisposable
{
    private readonly string _previousLinuxOverride;
    private readonly string _previousK8sOverride;
    private readonly string _previousWindowsOverride;

    public TentacleVersionRegistryTests()
    {
        // Snapshot any pre-existing values so the test process doesn't pollute
        // sibling tests that may share the env. Restored in Dispose().
        _previousLinuxOverride = Environment.GetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar);
        _previousK8sOverride = Environment.GetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar);
        _previousWindowsOverride = Environment.GetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar);

        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, null);
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar, null);
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, _previousLinuxOverride);
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar, _previousK8sOverride);
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, _previousWindowsOverride);
    }

    [Fact]
    public void OverrideEnvVar_LinuxConstantNamePinned()
    {
        // Renaming this constant breaks every air-gapped / canary deployment
        // that pinned a Linux tentacle version via env. Hard-pin in test.
        TentacleVersionRegistry.LinuxOverrideEnvVar.ShouldBe("SQUID_TARGET_LINUX_TENTACLE_VERSION");
    }

    [Fact]
    public void OverrideEnvVar_K8sConstantNamePinned()
    {
        TentacleVersionRegistry.K8sOverrideEnvVar.ShouldBe("SQUID_TARGET_K8S_AGENT_VERSION");
    }

    [Theory]
    [InlineData(nameof(CommunicationStyle.TentaclePolling), "1.4.2")]
    [InlineData(nameof(CommunicationStyle.TentacleListening), "1.4.2")]
    public async Task GetLatestVersionAsync_LinuxStyleWithEnvOverride_ReturnsOverrideValueWithoutHttp(string style, string expected)
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, expected);

        // Pass null HTTP factory — proves the override short-circuits BEFORE
        // any network IO (otherwise the registry would NPE on the HTTP path).
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(style, MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe(expected);
    }

    [Fact]
    public async Task GetLatestVersionAsync_K8sStyleWithEnvOverride_ReturnsOverrideValueWithoutHttp()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar, "2.0.0-canary.1");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.KubernetesAgent), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("2.0.0-canary.1");
    }

    [Fact]
    public async Task GetLatestVersionAsync_OverrideTrimmed_StripsWhitespace()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, "  1.4.2  ");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.4.2");
    }

    [Fact]
    public async Task GetLatestVersionAsync_UnknownStyle_ReturnsEmptyWithoutCrash()
    {
        // SSH targets (and any future style) shouldn't error the upgrade
        // pipeline; the orchestrator turns empty into a NotSupported
        // response with style name in the detail.
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync("Ssh", MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBeEmpty();
    }

    [Fact]
    public void Lifetime_RegistryIsScoped_NotSingleton()
    {
        // Critical correctness: ITentacleVersionRegistry MUST be IScopedDependency.
        //
        // The implementation injects ISquidHttpClientFactory, which is itself
        // IScopedDependency (it captures the request's ILifetimeScope to resolve
        // IHttpClientFactory). A singleton registry would capture the FIRST
        // request's scope forever; once that scope is disposed the factory
        // silently falls back to `new HttpClient()` per call → socket exhaustion.
        //
        // Pin both directions so a future "let's make it singleton again for
        // free caching" PR has to deliberately delete this test.
        typeof(IScopedDependency).IsAssignableFrom(typeof(ITentacleVersionRegistry))
            .ShouldBeTrue("registry must be scoped to avoid captive dependency on scoped HTTP factory");

        typeof(ISingletonDependency).IsAssignableFrom(typeof(ITentacleVersionRegistry))
            .ShouldBeFalse("registry must NOT be singleton — see captive-dependency note in interface XML doc");
    }

    // ========================================================================
    // Live HTTP path coverage. Until this section, every test short-circuited
    // via env override → the entire Docker Hub layer (URL routing,
    // pagination, semver picking, cache TTL, dedupe) was untested. This
    // section uses a stubbed HttpMessageHandler so we can drive deterministic
    // multi-page responses from a unit test, no network needed.
    // ========================================================================

    /// <summary>Captures requested URLs and replies with scripted bodies.</summary>
    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses;

        public List<string> RequestedUrls { get; } = new();

        public ScriptedHttpHandler(Dictionary<string, string> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            if (_responses.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (Mock<ISquidHttpClientFactory> factory, ScriptedHttpHandler handler) BuildScriptedFactory(Dictionary<string, string> responses)
    {
        var handler = new ScriptedHttpHandler(responses);
        var factory = new Mock<ISquidHttpClientFactory>();

        // Critical: disposeHandler:false. The registry uses `using var client = factory.CreateClient(...)`,
        // which disposes the HttpClient on scope exit — and that ALSO disposes the underlying handler
        // unless we opt out. With shared-handler tests (counting / sequenced responses) the second call
        // would hit a disposed handler → ObjectDisposedException → registry returns empty → false negative.
        factory
            .Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) });

        return (factory, handler);
    }

    private static string TagsPage(string nextUrl, params string[] tagNames)
    {
        var results = string.Join(",", tagNames.Select(t => $"{{\"name\":\"{t}\"}}"));
        var nextField = nextUrl == null ? "null" : $"\"{nextUrl}\"";
        return $"{{\"count\":{tagNames.Length},\"next\":{nextField},\"previous\":null,\"results\":[{results}]}}";
    }

    private const string LinuxFirstPageUrl = "https://hub.docker.com/v2/repositories/squidcd/squid-tentacle-linux/tags/?page_size=100&ordering=last_updated";
    private const string K8sFirstPageUrl = "https://hub.docker.com/v2/repositories/squidcd/squid-tentacle/tags/?page_size=100&ordering=last_updated";

    [Fact]
    public async Task LiveQuery_NoPagination_NextNull_ReturnsHighestFromSinglePage()
    {
        // Audit gap: the live HTTP path was 0% covered before this fix.
        // Single-page response, "next":null — most realistic case for early
        // Tentacle releases (<100 tags).
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: null, "1.4.0", "1.3.9", "1.4.1", "latest")
        };
        var (factory, handler) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.4.1");
        handler.RequestedUrls.Count.ShouldBe(1, "no `next` link → single HTTP call");
    }

    [Fact]
    public async Task LiveQuery_PaginationFollowsNextLink_PicksHighestAcrossAllPages()
    {
        // Audit N-3: previously the registry asked for page_size=100 and
        // ignored `next`. A repo with 250 tags would silently miss tags on
        // pages 2 and 3. Pin: highest semver intentionally placed on page 3
        // so a regression to "first-page-only" picks the wrong tag.
        TentacleVersionRegistry.ResetCacheForTests();
        const string page2Url = "https://hub.docker.com/v2/repositories/squidcd/squid-tentacle-linux/tags/?page=2&page_size=100";
        const string page3Url = "https://hub.docker.com/v2/repositories/squidcd/squid-tentacle-linux/tags/?page=3&page_size=100";
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: page2Url, "1.4.0", "1.4.1"),
            [page2Url] = TagsPage(nextUrl: page3Url, "1.4.2", "1.4.3"),
            [page3Url] = TagsPage(nextUrl: null, "1.5.0")            // ← highest, on page 3
        };
        var (factory, handler) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.5.0", "must scan all pages, not stop at page 1");
        handler.RequestedUrls.Count.ShouldBe(3);
        handler.RequestedUrls[1].ShouldBe(page2Url);
        handler.RequestedUrls[2].ShouldBe(page3Url);
    }

    [Fact]
    public async Task LiveQuery_PaginationCappedAt10Pages_DoesNotRunaway()
    {
        // Defence: even if Docker Hub returns 1000+ pages (bug, attack,
        // misconfig), don't hammer it indefinitely. Cap at MaxPagesScanned;
        // log a warning; return best-so-far. Use cycling next links so
        // every page returns a same-named "next" we can build deterministically.
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>();
        for (var i = 1; i <= 15; i++)
        {
            var url = i == 1 ? LinuxFirstPageUrl : $"https://hub.docker.com/v2/repositories/squidcd/squid-tentacle-linux/tags/?page={i}&page_size=100";
            var nextUrl = $"https://hub.docker.com/v2/repositories/squidcd/squid-tentacle-linux/tags/?page={i + 1}&page_size=100";
            responses[url] = TagsPage(nextUrl: nextUrl, $"1.{i}.0");
        }
        var (factory, handler) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        handler.RequestedUrls.Count.ShouldBe(TentacleVersionRegistry.MaxPagesScanned,
            $"must cap at {TentacleVersionRegistry.MaxPagesScanned} pages even if `next` keeps pointing further");
        version.ShouldBe($"1.{TentacleVersionRegistry.MaxPagesScanned}.0",
            "should still return the highest from the pages it DID scan");
    }

    [Fact]
    public async Task LiveQuery_K8sStyle_RoutesToK8sRepo_NotLinuxRepo()
    {
        // Audit H-16: routing test. A typo-swap of LinuxRepo and K8sRepo
        // constants would have ALL tests pass with the old all-overridden
        // suite. This pins the repo URL per style.
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: null, "9.9.9-LINUX-WRONG"),  // poisoned to fail loudly if linux is hit
            [K8sFirstPageUrl] = TagsPage(nextUrl: null, "2.0.0", "2.1.0")
        };
        var (factory, handler) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.KubernetesAgent), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("2.1.0");
        handler.RequestedUrls.ShouldAllBe(u => u.Contains("/squidcd/squid-tentacle/"),
            customMessage: "K8s style must hit squidcd/squid-tentacle, NOT squidcd/squid-tentacle-linux");
        handler.RequestedUrls.ShouldNotContain(u => u.Contains("squid-tentacle-linux"),
            customMessage: "K8s query MUST NOT touch the linux repo (would pick poisoned 9.9.9 tag if it did)");
    }

    [Fact]
    public async Task LiveQuery_LinuxStyle_RoutesToLinuxRepo()
    {
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: null, "1.4.0"),
            [K8sFirstPageUrl] = TagsPage(nextUrl: null, "9.9.9-K8S-WRONG")
        };
        var (factory, handler) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.4.0");
        handler.RequestedUrls.ShouldAllBe(u => u.Contains("squid-tentacle-linux"));
    }

    [Fact]
    public async Task LiveQuery_NonSemverTagsFiltered_GarbageInValidOut()
    {
        // 'latest', 'main', 'stable', 'v1.4.0' (leading v rejected) all
        // fail SemVer.TryParse → ignored. Only '1.4.0' picked.
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: null, "latest", "main", "stable", "v1.4.0", "1.4.0", "garbage")
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        (await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None))
            .ShouldBe("1.4.0");
    }

    [Theory]
    [InlineData("{\"count\":0,\"next\":null,\"previous\":null,\"results\":null}")]      // results: null
    [InlineData("{\"count\":0,\"next\":null,\"previous\":null,\"results\":42}")]        // results: int (garbage)
    [InlineData("{\"count\":0,\"next\":null,\"previous\":null,\"results\":\"oops\"}")]  // results: string
    public async Task LiveQuery_ResultsFieldNotArray_DegradesToEmptyWithoutThrowing(string body)
    {
        // Docker Hub edge responses / proxy weirdness could return a non-array
        // `results`. The old code's EnumerateArray would throw. The outer
        // try/catch would still swallow + degrade, but the explicit guard
        // keeps behaviour deterministic and the log clean (no exception
        // stack in ops tools).
        TentacleVersionRegistry.ResetCacheForTests();
        var (factory, _) = BuildScriptedFactory(new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = body
        });
        var registry = new TentacleVersionRegistry(factory.Object);

        // Must not throw; empty return is fine.
        var result = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task LiveQuery_DockerHubReturns404_FallsBackToEmptyGracefully()
    {
        // Repo doesn't exist / was deleted / Docker Hub down. Exception is
        // caught → null → no cache populated → empty result with warning.
        TentacleVersionRegistry.ResetCacheForTests();
        var (factory, _) = BuildScriptedFactory(new Dictionary<string, string>());  // every URL → 404
        var registry = new TentacleVersionRegistry(factory.Object);

        (await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None))
            .ShouldBeEmpty();
    }

    // ========================================================================
    // Round-8 pre-release filter.
    //
    // The release workflow (build-publish-linux-tentacle.yml) pushes
    // Docker Hub images on EVERY push to main — those builds get
    // GitVersion pre-release tags like "1.4.0-20". But the GitHub Release
    // (tarball) is only created on TAG pushes. Result: pre-release
    // versions exist on Docker Hub without a corresponding tarball on
    // GitHub Releases.
    //
    // Without a pre-release filter the registry would auto-pick
    // "1.4.0-20" as the latest semver, the bash upgrade script would
    // request /releases/download/1.4.0-20/… → 404 → exit 6 "URL not
    // reachable". Operator sees an inexplicable failure.
    //
    // Fix: auto-pick skips pre-release tags. Operators who legitimately
    // want a pre-release install still have the escape hatch —
    // body.targetVersion goes straight through the SemVer gate and
    // bypasses the registry entirely.
    // ========================================================================

    [Fact]
    public async Task LiveQuery_PreReleaseTagsSkipped_StableWinsEvenIfPreReleaseHasHigherSemver()
    {
        // Canonical scenario from the workflow gotcha: Docker Hub has an
        // in-progress pre-release AND a stable. Registry must pick stable.
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: null, "1.3.5", "1.4.0-20", "1.4.0-21", "1.4.0-rc.1")
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.3.5",
            "auto-pick must skip pre-release — even though 1.4.0-21 sorts higher by semver, its tarball doesn't exist on GitHub Releases");
    }

    [Fact]
    public async Task LiveQuery_OnlyPreReleaseTagsAvailable_ReturnsEmpty_NotAPreReleasePick()
    {
        // Brand-new release series that has only pre-release builds so far:
        // don't pick any of them. Operator must pin via body.targetVersion
        // if they want a pre-release on purpose.
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: null, "1.4.0-alpha.1", "1.4.0-alpha.2", "1.4.0-beta.1")
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBeEmpty(
            "no stable tags available → return empty, not a pre-release pick. Orchestrator surfaces this as Failed with operator guidance to set the version env.");
    }

    [Fact]
    public async Task LiveQuery_StableAmongPreReleases_HighestStableWinsAcrossPages()
    {
        // End-to-end: multiple pages, mix of stable + pre-release across them,
        // pick the highest STABLE — ignores higher pre-releases entirely.
        TentacleVersionRegistry.ResetCacheForTests();
        const string page2Url = "https://hub.docker.com/v2/repositories/squidcd/squid-tentacle-linux/tags/?page=2&page_size=100";
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: page2Url, "1.3.5", "1.4.0-rc.1", "1.4.0-rc.2"),
            [page2Url] = TagsPage(nextUrl: null, "1.4.0", "1.5.0-alpha.1")
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.4.0",
            "1.5.0-alpha.1 sorts higher by semver but is pre-release → skipped. 1.4.0 is the highest stable.");
    }

    [Fact]
    public async Task LiveQuery_BuildMetadataTagsAreNotPreRelease_StillPicked()
    {
        // Edge case worth locking in: "1.4.0+sha.abc" has build metadata
        // but is NOT a pre-release (per semver §10 build metadata doesn't
        // make a version pre-release). Registry MUST still pick it as a
        // valid stable release. Pre-release filter only blocks real
        // pre-release (the part after `-`).
        TentacleVersionRegistry.ResetCacheForTests();
        var responses = new Dictionary<string, string>
        {
            [LinuxFirstPageUrl] = TagsPage(nextUrl: null, "1.3.5", "1.4.0+sha.deadbeef")
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.4.0+sha.deadbeef",
            "build metadata does NOT make a version pre-release per semver §10 — must still be eligible for auto-pick");
    }

    // ========================================================================
    // Concurrent fan-out dedupe (audit N-4). Without this, 50 simultaneous
    // upgrade triggers on cold cache → 50 parallel Docker Hub queries → likely
    // rate-limit hit (100/6h anonymous). Lazy<Task<>> + GetOrAdd collapses
    // them to a single in-flight HTTP call.
    // ========================================================================

    /// <summary>HTTP handler that counts every call, optionally delaying responses to widen the race window.</summary>
    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly TimeSpan _delay;

        public int CallCount;

        public CountingHttpHandler(string body, TimeSpan delay)
        {
            _body = body;
            _delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);

            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) };
        }
    }

    [Fact]
    public async Task ConcurrentColdCache_FiftyCallers_CollapseToSingleHttpQuery()
    {
        // The bug: cold cache + N concurrent callers each fire their own HTTP
        // query. With dedupe, GetOrAdd returns the same Lazy<Task> to all N,
        // so only one underlying HTTP query runs and every caller awaits the
        // same result.
        TentacleVersionRegistry.ResetCacheForTests();
        var handler = new CountingHttpHandler(
            TagsPage(nextUrl: null, "1.4.0"),
            delay: TimeSpan.FromMilliseconds(50));   // wide enough to guarantee 50 callers race in
        var factory = new Mock<ISquidHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) });
        var registry = new TentacleVersionRegistry(factory.Object);

        // Fire 50 concurrent calls; await all.
        var tasks = Enumerable.Range(0, 50).Select(_ =>
            registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None)).ToArray();

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(v => v == "1.4.0");
        handler.CallCount.ShouldBe(1,
            "all 50 concurrent callers must collapse to ONE Docker Hub HTTP call (otherwise we trip the 100/6h anonymous rate limit on bulk upgrade)");
    }

    [Fact]
    public async Task ConcurrentColdCache_OneCallerCancels_OthersStillReceiveResult()
    {
        // Critical correctness: dedupe must NOT propagate one caller's
        // cancellation to siblings sharing the same in-flight task. Otherwise
        // any one caller giving up makes the whole pool fail.
        TentacleVersionRegistry.ResetCacheForTests();
        var handler = new CountingHttpHandler(
            TagsPage(nextUrl: null, "1.4.0"),
            delay: TimeSpan.FromMilliseconds(100));
        var factory = new Mock<ISquidHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) });
        var registry = new TentacleVersionRegistry(factory.Object);

        using var cts = new CancellationTokenSource();
        var cancellable = registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, cts.Token);
        var sibling = registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        cts.Cancel();   // kill caller A while task is still inflight

        await Should.ThrowAsync<OperationCanceledException>(async () => await cancellable);
        var siblingResult = await sibling;

        siblingResult.ShouldBe("1.4.0", "sibling's CT was never cancelled, so it should observe the completed query");
        handler.CallCount.ShouldBe(1, "still only one underlying HTTP call");
    }

    [Fact]
    public async Task PostCompletion_InFlightSlotReleased_NextColdCacheTriggersFreshQuery()
    {
        // After the in-flight Task completes, the dict slot must be removed,
        // so a future cold-cache (e.g. after manual cache reset) actually
        // re-queries instead of forever returning the stale Task result.
        TentacleVersionRegistry.ResetCacheForTests();
        var handler = new CountingHttpHandler(TagsPage(nextUrl: null, "1.4.0"), delay: TimeSpan.Zero);
        var factory = new Mock<ISquidHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) });
        var registry = new TentacleVersionRegistry(factory.Object);

        await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);
        TentacleVersionRegistry.ResetCacheForTests();   // simulate TTL expiry / restart between
        await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        handler.CallCount.ShouldBe(2,
            "second cold-cache request after in-flight slot release must perform a fresh query, not see a stale completed Task");
    }

    [Fact]
    public async Task ConcurrentColdCache_HttpQueryThrows_AllWaitersSeeFailureAndRetryWorksAfter()
    {
        // If the in-flight query faults, dedupe must:
        //  1. Surface the SAME failure to all waiters (not silently swallow)
        //  2. Remove the faulted Task from the dict so subsequent retries
        //     actually re-query (not stay stuck on the faulted Task forever)
        TentacleVersionRegistry.ResetCacheForTests();

        var attemptCount = 0;
        var failingHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        failingHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                Interlocked.Increment(ref attemptCount);

                // First call: fail; second call: succeed → proves retry isn't blocked by stale faulted Task
                return attemptCount == 1
                    ? throw new HttpRequestException("simulated transient failure")
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(TagsPage(nextUrl: null, "1.4.0"))
                    });
            });

        var factory = new Mock<ISquidHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(() => new HttpClient(failingHandler.Object, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) });
        var registry = new TentacleVersionRegistry(factory.Object);

        // First call: failure → registry catches and returns empty (graceful degrade)
        var firstResult = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);
        firstResult.ShouldBeEmpty("first attempt failed → registry returned empty");

        // Second call: must NOT be stuck on the faulted Task; should re-query and succeed
        var secondResult = await registry.GetLatestVersionAsync(nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);
        secondResult.ShouldBe("1.4.0", "second attempt must perform a fresh query, not return cached failure");
        attemptCount.ShouldBe(2);
    }

    // ========================================================================
    // Windows version source (separate from Linux/K8s).
    //
    // Why a separate method instead of a new CommunicationStyle case in
    // GetLatestVersionAsync(style):
    //   - Windows tentacles use the SAME CommunicationStyle values as Linux
    //     (TentaclePolling / TentacleListening) because they speak the same
    //     Halibut wire protocol. So `style` alone can't differentiate.
    //   -  will add OS-aware strategy resolution; until then,
    //     keeping the Windows version source as a separate explicit method
    //     side-steps the multi-strategy "exactly one owner" invariant in
    //     MachineUpgradeService.ResolveStrategy.
    //
    // GitHub Releases is the source-of-truth (not Docker Hub) because Phase
    // 12.E.0 ships .zip artefacts via GitHub Releases, with no Windows Docker
    // image planned (operator demand currently zero — Windows base images
    // are 6+ GB and the agent doesn't ship in a container).
    // ========================================================================

    [Fact]
    public void OverrideEnvVar_WindowsConstantNamePinned()
    {
        // Renaming this constant breaks every air-gapped / canary deployment
        // that pinned a Windows tentacle version via env. Hard-pin in test —
        // mirrors the LinuxOverrideEnvVar / K8sOverrideEnvVar discipline.
        TentacleVersionRegistry.WindowsOverrideEnvVar.ShouldBe("SQUID_TARGET_WINDOWS_TENTACLE_VERSION");
    }

    [Fact]
    public void GitHubReleasesUrl_ConstantPinned()
    {
        // The GitHub Releases REST URL pattern is hard-pinned because it
        // determines which repo's "latest" release is used as the Windows
        // version source-of-truth. Drift here would silently retarget
        // operators to the wrong repo's releases (e.g. a fork) without any
        // test failure on the implementation side.
        //
        // The /releases/latest endpoint returns the highest non-prerelease
        // tag — exactly what we want for Windows automatic version
        // resolution. To get prereleases too, operators set the env var
        // override (WindowsOverrideEnvVar) and pin a specific version.
        TentacleVersionRegistry.WindowsLatestReleaseUrl.ShouldBe(
            "https://api.github.com/repos/SolarifyDev/Squid/releases/latest");
    }

    [Fact]
    public async Task GetLatestWindowsVersionAsync_WithEnvOverride_ReturnsOverrideValueWithoutHttp()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, "1.6.0");

        // Pass null HTTP factory — proves the override short-circuits BEFORE
        // any network IO (otherwise the registry would NPE on the HTTP path).
        // Same defence as the Linux/K8s override tests.
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        version.ShouldBe("1.6.0");
    }

    [Fact]
    public async Task GetLatestWindowsVersionAsync_OverrideTrimmed_StripsWhitespace()
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, "  1.6.0  ");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        version.ShouldBe("1.6.0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetLatestWindowsVersionAsync_BlankOverride_FallsThroughToHttp(string blankValue)
    {
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, blankValue);

        // No HTTP factory + blank override → registry's live-query path is
        // disabled → method falls through to empty (graceful degrade).
        //
        // The behaviour pinned by this Theory: ResolveOverride uses
        // IsNullOrWhiteSpace as the "skip override" predicate, so both ""
        // and "   " fall through to the next resolution step rather than
        // being returned verbatim as a "version" of the empty/whitespace
        // value. The Linux side already handles this for the style-keyed
        // path; Windows must too. With no HTTP factory + override skipped,
        // the only remaining step is the empty-string fallback.
        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        // For whitespace ("   "): empty != "   " so this also implicitly
        // asserts the override didn't return the whitespace verbatim.
        // For empty (""): empty == "" — but the SAME pass is reached, so
        // the relevant invariant (empty fallback, no crash) holds either way.
        version.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLatestWindowsVersionAsync_GitHubApi_ReturnsTagNameFromLatestRelease()
    {
        // Real /releases/latest response shape (trimmed):
        //   {"url":"...","tag_name":"v1.6.0","name":"Squid Tentacle v1.6.0",...}
        // The `tag_name` field is what we extract. Leading "v" is stripped
        // because Squid GitHub Releases use both "v1.6.0" and "1.6.0" tag
        // styles depending on tagger (and the install-tentacle.sh fallback
        // chain in normalises both forms).
        var responses = new Dictionary<string, string>
        {
            [TentacleVersionRegistry.WindowsLatestReleaseUrl] =
                """{"tag_name":"v1.6.0","name":"Squid Tentacle v1.6.0","prerelease":false}"""
        };
        var (factory, handler) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        version.ShouldBe("1.6.0", "leading 'v' must be stripped to match install-tentacle.ps1's URL pattern");
        handler.RequestedUrls.Count.ShouldBe(1);
        handler.RequestedUrls[0].ShouldBe(TentacleVersionRegistry.WindowsLatestReleaseUrl);
    }

    [Fact]
    public async Task GetLatestWindowsVersionAsync_GitHubApi_TagWithoutVPrefix_PassedThrough()
    {
        // GitHub allows arbitrary tag names; not all of them are v-prefixed.
        // Squid's actual Linux tag history uses both forms; the install
        // script handles both. Registry must too.
        var responses = new Dictionary<string, string>
        {
            [TentacleVersionRegistry.WindowsLatestReleaseUrl] =
                """{"tag_name":"1.6.0","name":"v1.6.0","prerelease":false}"""
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        version.ShouldBe("1.6.0");
    }

    [Fact]
    public async Task GetLatestWindowsVersionAsync_GitHubApi_NotFound_ReturnsEmpty()
    {
        // Response 404 — no scripted entry → handler returns NotFound. Empty
        // GitHub release history is treated as "no version available" rather
        // than a hard error; the orchestrator surfaces an actionable
        // "could not resolve version" message to the operator.
        var (factory, _) = BuildScriptedFactory(new Dictionary<string, string>());
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        version.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLatestWindowsVersionAsync_GitHubApi_MalformedJson_ReturnsEmptyWithoutThrow()
    {
        // Malformed payload — registry must catch and degrade gracefully.
        // Mirrors the Linux side's "Docker Hub returned junk → log + empty"
        // pattern so the upgrade pipeline never crashes on a transient
        // upstream content-type / body issue.
        var responses = new Dictionary<string, string>
        {
            [TentacleVersionRegistry.WindowsLatestReleaseUrl] = "<!DOCTYPE html><html>not json</html>"
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        version.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetLatestWindowsVersionAsync_GitHubApi_MissingTagNameField_ReturnsEmpty()
    {
        // Defensive: a bug or schema drift on GitHub's side could remove the
        // tag_name field from the payload. Registry must NOT throw — must
        // degrade to empty and let the orchestrator render the "could not
        // resolve" message.
        var responses = new Dictionary<string, string>
        {
            [TentacleVersionRegistry.WindowsLatestReleaseUrl] =
                """{"name":"Squid Tentacle v1.6.0","prerelease":false}"""
        };
        var (factory, _) = BuildScriptedFactory(responses);
        var registry = new TentacleVersionRegistry(factory.Object);

        var version = await registry.GetLatestWindowsVersionAsync(CancellationToken.None);

        version.ShouldBeEmpty();
    }

    // ========================================================================
    // OS-aware routing through the unified public API.
    // The widened GetLatestVersionAsync(style, capabilities, ct) must route
    // Tentacle-style + Windows-OS callers to the GitHub Releases path,
    // and all other (style, OS) tuples to the legacy Docker Hub path.
    // Without this routing, a Windows agent's auto-version-resolution would
    // hit Docker Hub's `squidcd/squid-tentacle-linux` repo and either return
    // a Linux version (URL would 404 against GitHub Releases zip) or empty
    // (silent operator confusion).
    // ========================================================================

    [Fact]
    public async Task GetLatestVersionAsync_TentacleStyleWithWindowsOs_RoutesToGitHubReleases_NotDockerHub()
    {
        // The unified public method must dispatch by (style, OS) tuple. With
        // capabilities.Os = "Windows", the style isn't enough to pick the
        // version source — we have to hit GitHub Releases (where the .zip
        // ships), not Docker Hub (where only the Linux binary tags live).
        // Override env var pre-empts the live GitHub call so this test
        // doesn't actually need the network.
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, "1.6.0-windows");
        // Set the LINUX env var to a different value to PROVE the routing
        // didn't fall through to the Linux path.
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, "1.6.0-linux");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);
        var windowsCaps = new MachineRuntimeCapabilities { Os = "Windows" };

        var version = await registry.GetLatestVersionAsync(
            nameof(CommunicationStyle.TentaclePolling), windowsCaps, CancellationToken.None);

        version.ShouldBe("1.6.0-windows",
            "Windows-OS Tentacle must route to GitHub Releases / WindowsOverrideEnvVar — NOT to Linux Docker Hub");
    }

    [Fact]
    public async Task GetLatestVersionAsync_TentacleStyleWithLinuxOs_RoutesToDockerHub_NotGitHub()
    {
        // Symmetry: explicit Linux OS must STAY on the Docker Hub path.
        // Without this, Windows-routing would over-fire and break Linux
        // upgrades that previously worked.
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, "1.6.0-linux");
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, "1.6.0-windows");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);
        var linuxCaps = new MachineRuntimeCapabilities { Os = "Linux" };

        var version = await registry.GetLatestVersionAsync(
            nameof(CommunicationStyle.TentaclePolling), linuxCaps, CancellationToken.None);

        version.ShouldBe("1.6.0-linux",
            "Linux-OS Tentacle must continue routing to Docker Hub / LinuxOverrideEnvVar");
    }

    [Fact]
    public async Task GetLatestVersionAsync_TentacleStyleWithEmptyOs_RoutesToLinuxAsHistoricalDefault()
    {
        // Cold cache (capabilities.Os empty) preserves behaviour:
        // Linux strategy claims, Linux Docker Hub is the version source. This
        // mirrors the OS-aware strategy resolver's same default and keeps
        // existing Linux deployments working when the runtime cache hasn't
        // populated yet.
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, "1.6.0-linux");
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.WindowsOverrideEnvVar, "1.6.0-windows");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(
            nameof(CommunicationStyle.TentaclePolling), MachineRuntimeCapabilities.Empty, CancellationToken.None);

        version.ShouldBe("1.6.0-linux",
            "cold-cache (empty Os) defaults to Linux Docker Hub — preserves behaviour for the existing operator base");
    }

    [Fact]
    public async Task GetLatestVersionAsync_KubernetesAgent_IgnoresOsCapability()
    {
        // K8s tentacles always run Linux from the agent's perspective; the
        // OS axis is irrelevant. Mirror the strategy resolver's same
        // capabilities-ignored shape (KubernetesAgentUpgradeStrategy.CanHandle
        // doesn't read capabilities.Os).
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.K8sOverrideEnvVar, "1.6.0-k8s");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);
        var windowsCaps = new MachineRuntimeCapabilities { Os = "Windows" };   // bogus from K8s POV

        var version = await registry.GetLatestVersionAsync(
            nameof(CommunicationStyle.KubernetesAgent), windowsCaps, CancellationToken.None);

        version.ShouldBe("1.6.0-k8s",
            "K8s style must ignore OS capability — pods always run Linux from agent's perspective");
    }

    [Fact]
    public async Task GetLatestVersionAsync_NullCapabilities_TreatedAsEmpty_NoNullRefException()
    {
        // Defensive: a future caller passing null capabilities (instead of
        // MachineRuntimeCapabilities.Empty) must not NPE. Same fallback as
        // empty Os → Linux Docker Hub.
        Environment.SetEnvironmentVariable(TentacleVersionRegistry.LinuxOverrideEnvVar, "1.6.0-linux");

        var registry = new TentacleVersionRegistry(httpClientFactory: null);

        var version = await registry.GetLatestVersionAsync(
            nameof(CommunicationStyle.TentaclePolling), capabilities: null, CancellationToken.None);

        version.ShouldBe("1.6.0-linux");
    }
}
