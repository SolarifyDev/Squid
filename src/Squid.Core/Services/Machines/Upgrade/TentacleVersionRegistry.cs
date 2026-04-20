using System.Collections.Concurrent;
using System.Text.Json;
using Squid.Core.Services.Http;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines.Upgrade;

public sealed class TentacleVersionRegistry : ITentacleVersionRegistry
{
    public const string LinuxOverrideEnvVar = "SQUID_TARGET_LINUX_TENTACLE_VERSION";
    public const string K8sOverrideEnvVar = "SQUID_TARGET_K8S_AGENT_VERSION";

    private const string LinuxRepo = "squidcd/squid-tentacle-linux";
    private const string K8sRepo = "squidcd/squid-tentacle";

    /// <summary>
    /// Hard ceiling on Docker Hub pagination. 10 pages × 100 tags/page = 1000
    /// tags ≈ 5–10 years of Tentacle releases. Beyond this, log a warning and
    /// return the highest semver from the pages we did scan (degraded but
    /// non-blocking) rather than hammer Docker Hub indefinitely. Audit N-3.
    /// </summary>
    internal const int MaxPagesScanned = 10;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // Static so the TTL cache survives across the (now scoped-per-request)
    // registry instances — see lifetime note on ITentacleVersionRegistry.
    private static readonly ConcurrentDictionary<string, CachedVersion> _cache = new(StringComparer.OrdinalIgnoreCase);

    // In-flight dedupe (audit N-4): N concurrent cold-cache callers all hit
    // GetOrAdd on the same key → only the first one's factory creates the
    // Lazy → all N await the SAME underlying Task. After completion the
    // entry is removed so subsequent cold-cache periods re-query freshly.
    // Caller cancellation is wrapped via Task.WaitAsync(ct), so cancelling
    // one caller doesn't propagate to siblings sharing the in-flight task.
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private readonly ISquidHttpClientFactory _httpClientFactory;

    public TentacleVersionRegistry(ISquidHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetLatestVersionAsync(string communicationStyle, CancellationToken ct)
    {
        var source = ResolveSource(communicationStyle);

        if (source == null) return WarnUnknownStyle(communicationStyle);

        return ResolveOverride(source.EnvVar)
            ?? ResolveFreshCache(source.DockerRepo)
            ?? await ResolveLiveAndCacheAsync(source.DockerRepo, ct).ConfigureAwait(false)
            ?? ResolveStaleCache(source.DockerRepo)
            ?? WarnExhausted(communicationStyle);
    }

    // ── Step 1: env override ─────────────────────────────────────────────────

    private static string ResolveOverride(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    // ── Step 2: fresh in-process cache ───────────────────────────────────────

    private string ResolveFreshCache(string dockerRepo)
    {
        return _cache.TryGetValue(dockerRepo, out var cached) && cached.IsFresh ? cached.Version : null;
    }

    // ── Step 3: live Docker Hub query → cache result (with fan-out dedupe) ──

    private async Task<string> ResolveLiveAndCacheAsync(string dockerRepo, CancellationToken ct)
    {
        // Atomic dedupe: many concurrent callers see the SAME Lazy<Task>
        // and end up awaiting the SAME underlying HTTP query. The first
        // caller's factory invocation triggers the inner Task lazily.
        var lazy = _inFlight.GetOrAdd(dockerRepo, repo => new Lazy<Task<string>>(
            () => QueryAndPopulateCacheAsync(repo),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // WaitAsync respects THIS caller's cancellation token without
            // propagating it to the underlying task — so cancelling one
            // caller doesn't fail the 49 siblings sharing the same query.
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Drop the in-flight slot only if it's still pointing at OUR Lazy.
            // KVP-typed TryRemove is the atomic compare-and-remove primitive
            // — without it, a slow finally could remove a fresh Lazy spawned
            // by a later cold-cache window.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(dockerRepo, lazy));
        }
    }

    private async Task<string> QueryAndPopulateCacheAsync(string dockerRepo)
    {
        // Inner task uses CancellationToken.None — the per-page HTTP timeout
        // (RequestTimeout = 10s) is the upper bound. This decoupling is what
        // lets one caller cancel without breaking siblings (see WaitAsync above).
        var version = await QueryDockerHubLatestSemverTagAsync(dockerRepo, CancellationToken.None).ConfigureAwait(false);

        if (string.IsNullOrEmpty(version)) return null;

        _cache[dockerRepo] = new CachedVersion(version, DateTimeOffset.UtcNow);

        return version;
    }

    // ── Step 4: stale cache (Docker Hub down → degrade gracefully) ───────────

    private string ResolveStaleCache(string dockerRepo)
    {
        if (!_cache.TryGetValue(dockerRepo, out var cached)) return null;

        Log.Warning("[Upgrade] Docker Hub query for {Repo} failed; falling back to stale cached version {Version} (cached {Age} ago)",
            dockerRepo, cached.Version, DateTimeOffset.UtcNow - cached.CachedAt);

        return cached.Version;
    }

    // ── Step 5: nothing worked → empty + loud warning ────────────────────────

    private static string WarnExhausted(string style)
    {
        Log.Warning("[Upgrade] Could not resolve latest tentacle version for style '{Style}' " +
                    "(no override, no cache, Docker Hub unreachable). Operator must pass TargetVersion explicitly.", style);

        return string.Empty;
    }

    private static string WarnUnknownStyle(string style)
    {
        Log.Warning("[Upgrade] No version source registered for CommunicationStyle '{Style}'", style);

        return string.Empty;
    }

    // ── Source routing: style → (env var, docker repo) ──────────────────────

    private static VersionSource ResolveSource(string communicationStyle) => communicationStyle switch
    {
        nameof(CommunicationStyle.TentaclePolling)
            or nameof(CommunicationStyle.TentacleListening) => new(LinuxOverrideEnvVar, LinuxRepo),

        nameof(CommunicationStyle.KubernetesAgent) => new(K8sOverrideEnvVar, K8sRepo),

        _ => null
    };

    // ── HTTP layer: paginate tag list, pick highest semver ──────────────────

    private async Task<string> QueryDockerHubLatestSemverTagAsync(string dockerRepo, CancellationToken ct)
    {
        try
        {
            return await PaginateAndPickHighestAsync(dockerRepo, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Upgrade] Failed to query Docker Hub for repo {Repo}", dockerRepo);

            return null;
        }
    }

    /// <summary>
    /// Walks Docker Hub's <c>next</c> link chain, accumulating the highest
    /// semver tag across every page. Stops at <see cref="MaxPagesScanned"/>
    /// (with a loud warning) so a runaway / cyclical pagination response
    /// can't burn unlimited HTTP round-trips.
    /// </summary>
    private async Task<string> PaginateAndPickHighestAsync(string dockerRepo, CancellationToken ct)
    {
        var url = BuildFirstPageUrl(dockerRepo);

        using var client = _httpClientFactory.CreateClient(timeout: RequestTimeout);

        SemVer winner = null;

        for (var page = 1; page <= MaxPagesScanned; page++)
        {
            var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);

            winner = MergeHighestFromPage(doc.RootElement, winner);

            var nextUrl = ReadNextPageUrl(doc.RootElement);

            if (string.IsNullOrEmpty(nextUrl)) return winner?.Raw;

            url = nextUrl;
        }

        Log.Warning(
            "[Upgrade] Docker Hub repo {Repo} has more than {MaxPages} pages of tags ({MaxTagsScanned} tags scanned). " +
            "Returning highest semver from scanned pages; tags beyond this window are NOT considered. " +
            "Either prune old tags or raise the cap.",
            dockerRepo, MaxPagesScanned, MaxPagesScanned * 100);

        return winner?.Raw;
    }

    private static string BuildFirstPageUrl(string dockerRepo)
        => $"https://hub.docker.com/v2/repositories/{dockerRepo}/tags/?page_size=100&ordering=last_updated";

    private static string ReadNextPageUrl(JsonElement root)
    {
        if (!root.TryGetProperty("next", out var nextEl)) return null;

        return nextEl.ValueKind == JsonValueKind.String ? nextEl.GetString() : null;
    }

    private static SemVer MergeHighestFromPage(JsonElement root, SemVer currentWinner)
    {
        if (!root.TryGetProperty("results", out var results)) return currentWinner;

        // Docker Hub responses with `"results": null` (rare: empty repo, odd
        // rate-limit quirk) would make EnumerateArray throw. The outer catch
        // would swallow it and degrade gracefully, but a precise guard gives
        // a better log (no exception stack) and keeps behaviour deterministic.
        if (results.ValueKind != JsonValueKind.Array) return currentWinner;

        var winner = currentWinner;

        foreach (var tag in results.EnumerateArray())
        {
            var parsed = TryReadSemverTag(tag);

            if (parsed != null && (winner == null || parsed.CompareTo(winner) > 0))
                winner = parsed;
        }

        return winner;
    }

    private static SemVer TryReadSemverTag(JsonElement tag)
    {
        if (!tag.TryGetProperty("name", out var nameElement)) return null;

        // SemVer.TryParse rejects "latest", 2-component "1.4", 4-component
        // "1.4.0.0", and any tag with shell metacharacters — so a poisoned
        // Docker Hub tag (compromised maintainer pushes `1.4.0";rm -rf /;#`)
        // can't be picked as the winner and reach the bash template.
        if (!SemVer.TryParse(nameElement.GetString(), out var parsed)) return null;

        // Round-8: auto-pick skips pre-release tags. The release workflow
        // (build-publish-linux-tentacle.yml) pushes main-branch builds to
        // Docker Hub as pre-release versions (e.g. "1.4.0-20") but only
        // creates the GitHub Release tarball on TAG pushes — so picking a
        // pre-release here would give the bash script a download URL that
        // doesn't exist (exit 6). Build metadata (`1.4.0+sha.abc`) is NOT
        // pre-release per semver §10 — stays eligible.
        //
        // Operators who deliberately want a pre-release install pass
        // `targetVersion: "1.4.0-beta.1"` in the upgrade request body —
        // that path bypasses the registry and goes straight through the
        // SemVer boundary gate.
        if (parsed.IsPreRelease) return null;

        return parsed;
    }

    /// <summary>
    /// Test-only hook to drop both static dicts between cases. Internal so it's
    /// not on the production surface. Includes the in-flight dedupe dict so a
    /// previous test's lingering Lazy can't bleed into the next test.
    /// </summary>
    internal static void ResetCacheForTests()
    {
        _cache.Clear();
        _inFlight.Clear();
    }

    private sealed record VersionSource(string EnvVar, string DockerRepo);

    private sealed record CachedVersion(string Version, DateTimeOffset CachedAt)
    {
        public bool IsFresh => DateTimeOffset.UtcNow - CachedAt < CacheTtl;
    }
}
