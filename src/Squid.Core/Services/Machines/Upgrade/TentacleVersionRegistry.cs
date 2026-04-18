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

    private const string DownloadUrlTemplate =
        "https://github.com/SolarifyDev/Squid/releases/download/{0}/squid-tentacle-{0}-{1}.tar.gz";

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
        if (source == null)
        {
            Log.Warning("[Upgrade] No version source registered for CommunicationStyle '{Style}'", communicationStyle);
            return string.Empty;
        }

        // 1. Operator override always wins — covers air-gap, canary, fork.
        var overrideValue = Environment.GetEnvironmentVariable(source.EnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return overrideValue.Trim();

        // 2. Cache hit
        if (_cache.TryGetValue(source.DockerRepo, out var cached) && cached.IsFresh)
            return cached.Version;

        // 3. Live query
        var fresh = await QueryDockerHubLatestSemverTagAsync(source.DockerRepo, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(fresh))
        {
            _cache[source.DockerRepo] = new CachedVersion(fresh, DateTimeOffset.UtcNow);
            return fresh;
        }

        // 4. Stale cache fallback — better to return last-known-good than to
        //    refuse the entire upgrade just because Docker Hub burped.
        if (cached != null)
        {
            Log.Warning("[Upgrade] Docker Hub query for {Repo} failed; falling back to stale cached version {Version} (cached {Age} ago)",
                source.DockerRepo, cached.Version, DateTimeOffset.UtcNow - cached.CachedAt);
            return cached.Version;
        }

        // 5. Hard failure — operator must specify TargetVersion explicitly.
        Log.Warning("[Upgrade] Could not resolve latest tentacle version for style '{Style}' (no override, no cache, Docker Hub unreachable). " +
                    "Operator must pass TargetVersion explicitly.", communicationStyle);
        return string.Empty;
    }

    public string GetLinuxDownloadUrl(string version, string rid)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version is required", nameof(version));
        if (string.IsNullOrWhiteSpace(rid))
            throw new ArgumentException("rid is required", nameof(rid));

        return string.Format(DownloadUrlTemplate, version.Trim(), rid.Trim());
    }

    private static VersionSource ResolveSource(string communicationStyle) => communicationStyle switch
    {
        nameof(CommunicationStyle.TentaclePolling) or nameof(CommunicationStyle.TentacleListening)
            => new VersionSource(LinuxOverrideEnvVar, LinuxRepo),
        nameof(CommunicationStyle.KubernetesAgent)
            => new VersionSource(K8sOverrideEnvVar, K8sRepo),
        _ => null
    };

    /// <summary>
    /// Queries Docker Hub's tags endpoint, parses semver names, returns the
    /// numerically-highest. Ignores tags that don't parse as <see cref="Version"/>
    /// (skips <c>latest</c>, suffix-only tags like <c>1.4.0-amd64</c>).
    /// </summary>
    private async Task<string> QueryDockerHubLatestSemverTagAsync(string dockerRepo, CancellationToken ct)
    {
        var url = $"https://hub.docker.com/v2/repositories/{dockerRepo}/tags/?page_size=100&ordering=last_updated";

        try
        {
            using var client = _httpClientFactory.CreateClient(timeout: RequestTimeout);
            var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return null;

            string latest = null;
            Version latestParsed = null;

            foreach (var tag in results.EnumerateArray())
            {
                if (!tag.TryGetProperty("name", out var nameElement)) continue;
                var name = nameElement.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!Version.TryParse(name, out var parsed)) continue;

                if (latestParsed == null || parsed > latestParsed)
                {
                    latestParsed = parsed;
                    latest = name;
                }
            }

            return latest;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Upgrade] Failed to query Docker Hub for latest tentacle version: {Url}", url);
            return null;
        }
    }

    private sealed record VersionSource(string EnvVar, string DockerRepo);

    private sealed record CachedVersion(string Version, DateTimeOffset CachedAt)
    {
        public bool IsFresh => DateTimeOffset.UtcNow - CachedAt < CacheTtl;
    }
}
