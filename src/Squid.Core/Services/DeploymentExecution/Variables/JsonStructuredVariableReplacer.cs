using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.DeploymentExecution.Variables;

internal static class JsonStructuredVariableReplacer
{
    internal static byte[] ReplaceInJsonFile(byte[] content, Dictionary<string, string> replacements)
    {
        if (content == null || content.Length == 0) return content;

        using var doc = JsonDocument.Parse(content);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        TraverseAndReplace(doc.RootElement, "", replacements, writer);

        writer.Flush();
        return stream.ToArray();
    }

    private static void TraverseAndReplace(JsonElement element, string parentPath, Dictionary<string, string> replacements, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    var path = BuildPath(parentPath, property.Name);
                    writer.WritePropertyName(property.Name);
                    TraverseAndReplace(property.Value, path, replacements, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var path = BuildPath(parentPath, index.ToString());
                    TraverseAndReplace(item, path, replacements, writer);
                    index++;
                }
                writer.WriteEndArray();
                break;

            default:
                if (!string.IsNullOrEmpty(parentPath) && TryGetReplacement(parentPath, replacements, out var newValue))
                    WriteReplacementValue(writer, element, newValue);
                else
                    element.WriteTo(writer);
                break;
        }
    }

    private static bool TryGetReplacement(string path, Dictionary<string, string> replacements, out string value)
    {
        foreach (var kvp in replacements)
        {
            if (string.Equals(kvp.Key, path, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void WriteReplacementValue(Utf8JsonWriter writer, JsonElement original, string newValue)
    {
        switch (original.ValueKind)
        {
            case JsonValueKind.Number:
                if (long.TryParse(newValue, out var longVal))
                {
                    writer.WriteNumberValue(longVal);
                    return;
                }
                if (double.TryParse(newValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleVal))
                {
                    writer.WriteNumberValue(doubleVal);
                    return;
                }
                writer.WriteStringValue(newValue);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                if (bool.TryParse(newValue, out var boolVal))
                {
                    writer.WriteBooleanValue(boolVal);
                    return;
                }
                writer.WriteStringValue(newValue);
                break;

            default:
                TryWriteJsonStructure(writer, newValue);
                break;
        }
    }

    private static void TryWriteJsonStructure(Utf8JsonWriter writer, string newValue)
    {
        if (newValue != null && (newValue.StartsWith('{') || newValue.StartsWith('[')))
        {
            try
            {
                using var parsed = JsonDocument.Parse(newValue);
                parsed.RootElement.WriteTo(writer);
                return;
            }
            catch (JsonException)
            {
                // not valid JSON — fall through to write as string
            }
        }

        writer.WriteStringValue(newValue);
    }

    private static string BuildPath(string parent, string key)
    {
        if (string.IsNullOrEmpty(parent)) return key;

        return parent + ":" + key;
    }
}
