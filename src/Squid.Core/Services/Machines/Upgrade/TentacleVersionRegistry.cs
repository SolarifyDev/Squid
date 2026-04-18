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

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, CachedVersion> _cache = new(StringComparer.OrdinalIgnoreCase);

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

    // ── Step 3: live Docker Hub query → cache result ─────────────────────────

    private async Task<string> ResolveLiveAndCacheAsync(string dockerRepo, CancellationToken ct)
    {
        var version = await QueryDockerHubLatestSemverTagAsync(dockerRepo, ct).ConfigureAwait(false);

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

    // ── HTTP layer: fetch tag list, pick highest semver ─────────────────────

    private async Task<string> QueryDockerHubLatestSemverTagAsync(string dockerRepo, CancellationToken ct)
    {
        try
        {
            var json = await FetchTagsJsonAsync(dockerRepo, ct).ConfigureAwait(false);

            return PickHighestSemverTag(json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Upgrade] Failed to query Docker Hub for repo {Repo}", dockerRepo);

            return null;
        }
    }

    private async Task<string> FetchTagsJsonAsync(string dockerRepo, CancellationToken ct)
    {
        var url = $"https://hub.docker.com/v2/repositories/{dockerRepo}/tags/?page_size=100&ordering=last_updated";

        using var client = _httpClientFactory.CreateClient(timeout: RequestTimeout);

        return await client.GetStringAsync(url, ct).ConfigureAwait(false);
    }

    private static string PickHighestSemverTag(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results)) return null;

        string winner = null;
        Version winnerParsed = null;

        foreach (var tag in results.EnumerateArray())
        {
            var (name, parsed) = TryReadSemverTag(tag);

            if (parsed != null && (winnerParsed == null || parsed > winnerParsed))
            {
                winnerParsed = parsed;
                winner = name;
            }
        }

        return winner;
    }

    private static (string Name, Version Parsed) TryReadSemverTag(JsonElement tag)
    {
        if (!tag.TryGetProperty("name", out var nameElement)) return (null, null);

        var name = nameElement.GetString();

        if (string.IsNullOrEmpty(name) || !Version.TryParse(name, out var parsed)) return (null, null);

        return (name, parsed);
    }

    private sealed record VersionSource(string EnvVar, string DockerRepo);

    private sealed record CachedVersion(string Version, DateTimeOffset CachedAt)
    {
        public bool IsFresh => DateTimeOffset.UtcNow - CachedAt < CacheTtl;
    }
}
