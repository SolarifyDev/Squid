namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The live source-map rows of one space, keyed by normalised (source type, source id). Rows whose
/// destination no longer exists are already dropped by the loader, so every entry here resolves.
/// </summary>
public sealed class OctopusImportPriorMappings
{
    public static OctopusImportPriorMappings Empty { get; } = new(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(), null);

    public OctopusImportPriorMappings(IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> mappings, string installationId)
    {
        Mappings = Normalize(mappings);
        InstallationId = installationId;
    }

    public IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> Mappings { get; }

    /// <summary>The Octopus installation this space's history was fetched from, or null when none is recorded.</summary>
    public string InstallationId { get; }

    public bool TryGet(string sourceType, string sourceId, out OctopusImportPriorMapping mapping)
        => Mappings.TryGetValue(Key(sourceType, sourceId), out mapping);

    /// <summary>The one normalisation every source-map key uses: trimmed, upper-case invariant (same as the session id map).</summary>
    public static string Normalize(string value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    public static (string Type, string Id) Key(string sourceType, string sourceId) => (Normalize(sourceType), Normalize(sourceId));

    private static Dictionary<(string Type, string Id), OctopusImportPriorMapping> Normalize(IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> mappings)
        => (mappings ?? new Dictionary<(string Type, string Id), OctopusImportPriorMapping>()).ToDictionary(pair => Key(pair.Key.Type, pair.Key.Id), pair => pair.Value);
}
