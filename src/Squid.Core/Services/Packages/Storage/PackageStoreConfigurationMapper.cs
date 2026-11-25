using System.Text.Json;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>Between the JSON columns of a store record and the dictionaries a provider works with.</summary>
public static class PackageStoreConfigurationMapper
{
    public static PackageBlobStoreConfiguration ToConfiguration(PackageStore record)
    {
        return new PackageBlobStoreConfiguration { Properties = ParseSettings(record.Properties), Secrets = ParseSettings(record.Secret) };
    }

    /// <summary>An absent or empty column is no settings. A null value inside the JSON is kept as an empty string.</summary>
    public static Dictionary<string, string> ParseSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

        return parsed.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty);
    }

    /// <summary>Like <see cref="ParseSettings"/>, but a column that is not valid settings JSON counts as no settings, so a broken row can still be repaired by an edit.</summary>
    public static Dictionary<string, string> ParseSettingsLeniently(string json)
    {
        try
        {
            return ParseSettings(json);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Null when there are no settings, so the column stays null rather than holding "{}".</summary>
    public static string SerializeSettings(IReadOnlyDictionary<string, string> settings)
    {
        return settings.Count == 0 ? null : JsonSerializer.Serialize(settings);
    }
}
