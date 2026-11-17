using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// Sanitised synthetic responses of an Octopus Server 2026.1 REST API (one space, two database-backed projects), stored
/// under <c>tests/Fixtures/OctopusServer2026</c>. Every file has the JSON shape a real 2026.1.11242 server returned for the
/// same endpoint; names, ids, hosts and values are fake. File name = endpoint path below <c>api/Spaces-1/</c> with
/// <c>/</c> replaced by <c>-</c> (root endpoints drop the space). Projects-2 also carries two shapes that real servers emit
/// but the reference capture did not contain: a sensitive action property and a prompted select-list variable.
/// </summary>
internal static class OctopusServer2026Fixtures
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "OctopusServer2026");

    public static IReadOnlyList<string> Names => System.IO.Directory.GetFiles(Directory, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();

    public static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name + ".json"));

    public static JsonNode Node(string name) => JsonNode.Parse(Read(name));

    /// <summary>The resources of a fixture: the array itself, the <c>Items</c> of a page, or the single document.</summary>
    public static IReadOnlyList<JsonObject> Resources(string name)
    {
        var node = Node(name);

        if (node is JsonArray array) return array.OfType<JsonObject>().ToList();
        if (node is JsonObject page && page["Items"] is JsonArray items) return items.OfType<JsonObject>().ToList();

        return [(JsonObject)node];
    }
}
