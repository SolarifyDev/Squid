using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Squid.Core.Setup.Configuration;

/// <summary>
/// The configuration file Squid writes for itself in the data directory (<c>squid.json</c>).
/// Values are addressed by ordinary configuration keys (<c>Section:Key</c>) so whatever is merged
/// here is read back by the existing settings classes without any change to them.
/// </summary>
public static class SquidConfigurationFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Merge(string path, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return;

        var root = Load(path);

        foreach (var (key, value) in values) SetValue(root, key, value);

        OwnerOnlyFile.WriteAtomically(path, root.ToJsonString(WriteOptions));
    }

    /// <summary>Reads one value straight from the file (bypassing any configuration snapshot taken before it was written).</summary>
    public static string GetValue(string path, string key)
    {
        JsonNode node = Load(path);

        foreach (var segment in key.Split(':')) node = (node as JsonObject)?[segment];

        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    public static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
    }

    private static void SetValue(JsonObject root, string key, string value)
    {
        var segments = key.Split(':');
        var node = root;

        foreach (var segment in segments[..^1]) node = ChildObject(node, segment);

        node[segments[^1]] = value;
    }

    private static JsonObject ChildObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;

        var created = new JsonObject();
        parent[name] = created;

        return created;
    }

}
