using System.Text.Json;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Machines;

public interface IAgentVersionProvider : IScopedDependency
{
    Task<string> GetLatestKubernetesAgentVersionAsync(CancellationToken ct);
}

public class AgentVersionProvider : IAgentVersionProvider
{
    private const string DockerHubUrl = "https://hub.docker.com/v2/repositories/squidcd/squid-tentacle/tags/?page_size=100&ordering=last_updated";
    
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ISquidHttpClientFactory _httpClientFactory;

    private static string _cachedVersion;
    private static DateTimeOffset _cacheExpiry;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public AgentVersionProvider(ISquidHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetLatestKubernetesAgentVersionAsync(CancellationToken ct)
    {
        if (_cachedVersion != null && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cachedVersion;

        await Lock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_cachedVersion != null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cachedVersion;

            var version = await FetchLatestKubernetesAgentVersionAsync(ct).ConfigureAwait(false);

            _cachedVersion = version;
            _cacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);

            return version;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch latest agent version from Docker Hub");
            return _cachedVersion;
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task<string> FetchLatestKubernetesAgentVersionAsync(CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient(timeout: RequestTimeout);

        var json = await client.GetStringAsync(DockerHubUrl, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");

        string latest = null;
        Version latestParsed = null;

        foreach (var tag in results.EnumerateArray())
        {
            var name = tag.GetProperty("name").GetString();
            if (name == null) continue;
            if (!Version.TryParse(name, out var parsed)) continue;

            if (latestParsed == null || parsed > latestParsed)
            {
                latestParsed = parsed;
                latest = name;
            }
        }

        return latest;
    }
}
