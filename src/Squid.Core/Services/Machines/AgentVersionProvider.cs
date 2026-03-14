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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ISquidHttpClientFactory _httpClientFactory;

    public AgentVersionProvider(ISquidHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetLatestKubernetesAgentVersionAsync(CancellationToken ct)
    {
        try
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch latest agent version from Docker Hub");
            return null;
        }
    }
}
