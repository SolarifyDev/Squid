using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>Reads and writes the session's <c>source_summary_json</c> in the shape the session service stores it.</summary>
public static class OctopusImportSourceSummaries
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static OctopusImportSourceSummaryDto Parse(string json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<OctopusImportSourceSummaryDto>(json, JsonOptions);
}
