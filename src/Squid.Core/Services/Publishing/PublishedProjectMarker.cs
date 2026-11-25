using System.Text.Json;

namespace Squid.Core.Services.Publishing;

/// <summary>
/// What marks a project as created by publishing a package, kept in the project's free-form <c>Json</c> column (no schema change): which package it runs, which recipe
/// made its process, and a fingerprint of the plan that process was built from. Publishing again compares the fingerprint, so an unchanged plan leaves the process alone.
/// A project whose <c>Json</c> is anything else (an Octopus import, an empty string) has no marker.
/// </summary>
public sealed record PublishedProjectMarker(string PackageId, string Recipe, string PlanHash)
{
    public const string JsonKey = "squidPublish";

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object> { [JsonKey] = new Dictionary<string, string> { ["packageId"] = PackageId, ["recipe"] = Recipe, ["planHash"] = PlanHash } });

    public static PublishedProjectMarker TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(JsonKey, out var node) ? FromNode(node) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PublishedProjectMarker FromNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;

        var packageId = Text(node, "packageId");

        return string.IsNullOrEmpty(packageId) ? null : new PublishedProjectMarker(packageId, Text(node, "recipe"), Text(node, "planHash"));
    }

    private static string Text(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
