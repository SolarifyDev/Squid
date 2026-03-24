using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public static class ExternalFeedProperties
{
    // Container registry
    public static string GetApiVersion(ExternalFeed feed) => GetString(feed, "ApiVersion");
    public static string GetRegistryPath(ExternalFeed feed) => GetString(feed, "RegistryPath");

    // Download retry (NuGet, Maven, GitHub)
    public static int GetDownloadAttempts(ExternalFeed feed, int defaultValue = 5) =>
        GetInt(feed, "DownloadAttempts", defaultValue);

    public static int GetDownloadRetryBackoffSeconds(ExternalFeed feed, int defaultValue = 10) =>
        GetInt(feed, "DownloadRetryBackoffSeconds", defaultValue);

    // NuGet
    public static bool GetEnhancedMode(ExternalFeed feed, bool defaultValue = false) =>
        GetBool(feed, "EnhancedMode", defaultValue);

    // Generic accessors
    public static string GetString(ExternalFeed feed, string key)
    {
        var dict = ParseAll(feed);
        return dict.TryGetValue(key, out var value) ? value : null;
    }

    public static int GetInt(ExternalFeed feed, string key, int defaultValue)
    {
        var raw = GetString(feed, key);
        return int.TryParse(raw, out var value) ? value : defaultValue;
    }

    public static bool GetBool(ExternalFeed feed, string key, bool defaultValue)
    {
        var raw = GetString(feed, key);
        return bool.TryParse(raw, out var value) ? value : defaultValue;
    }

    public static Dictionary<string, string> ParseAll(ExternalFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed?.Properties))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(feed.Properties)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string Serialize(Dictionary<string, string> properties)
    {
        if (properties == null || properties.Count == 0)
            return null;

        return JsonSerializer.Serialize(properties);
    }
}
