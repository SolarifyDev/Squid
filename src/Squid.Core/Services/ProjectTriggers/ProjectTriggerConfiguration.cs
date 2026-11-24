using System.Text.Json;

namespace Squid.Core.Services.ProjectTriggers;

public static class ProjectTriggerConfiguration
{
    public static void Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("A trigger configuration is required.");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
                throw new ArgumentException("Trigger configuration requires numeric schemaVersion 1.");

            CheckUniqueProperties(root);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid trigger configuration JSON.");
        }
    }

    // jsonb canonicalizes duplicate keys. Reject ambiguous input before validation and storage can disagree.
    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                CheckUniqueProperties(item);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("Trigger configuration contains duplicate property names.");

                CheckUniqueProperties(property.Value);
            }
        }
    }
}
