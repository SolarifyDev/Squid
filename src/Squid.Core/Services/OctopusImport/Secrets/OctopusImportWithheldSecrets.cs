using System.Text.Json;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Reads the <c>Squid.Import.WithheldSecrets</c> marker the REST normaliser writes on the object that owns a withheld secret
/// (feed root, variable, action, account <c>Credentials</c>): the single source of WHICH members the Octopus server did not
/// return. The marker alone never lowers a severity — only a server session does (<see cref="OctopusImportSecretSeverity"/>) —
/// so a file that plants one can only make the import more cautious (a value treated as missing), never less.
/// </summary>
public static class OctopusImportWithheldSecrets
{
    public const string Property = OctopusRestDocumentNormalizer.WithheldSecretsProperty;

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Member names withheld on the object whose extension data this is; empty when there is no (well-formed) marker.</summary>
    public static IReadOnlySet<string> For(IReadOnlyDictionary<string, JsonElement> extensionData)
        => extensionData != null && extensionData.TryGetValue(Property, out var marker) ? Names(marker) : None;

    /// <summary>Member names withheld inside a JSON object (an account's <c>Credentials</c>).</summary>
    public static IReadOnlySet<string> For(JsonElement? element)
        => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(Property, out var marker) ? Names(marker) : None;

    public static bool IsWithheld(IReadOnlyDictionary<string, JsonElement> extensionData, string member) => For(extensionData).Contains(member);

    private static IReadOnlySet<string> Names(JsonElement marker)
    {
        if (marker.ValueKind != JsonValueKind.Array) return None;

        return marker.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())).Select(e => e.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
