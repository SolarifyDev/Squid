namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The live source-map rows of one space, keyed by normalised (source type, source id). Rows whose
/// destination no longer exists are already dropped by the loader, so every entry here resolves.
/// </summary>
public sealed class OctopusImportPriorMappings
{
    public static OctopusImportPriorMappings Empty { get; } = new(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(), null);

    public OctopusImportPriorMappings(IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> mappings, string installationId, IReadOnlyList<string> exportSourceIds = null)
    {
        Mappings = Normalize(mappings);
        InstallationId = installationId;
        ExportSourceIds = exportSourceIds ?? [];
    }

    public IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> Mappings { get; }

    /// <summary>The Octopus installation this space's history was fetched from, or null when none is recorded.</summary>
    public string InstallationId { get; }

    /// <summary>The export-source fingerprints (export id suffixes) recorded for this space, oldest first; empty when no suffixed export was imported.</summary>
    public IReadOnlyList<string> ExportSourceIds { get; }

    public bool TryGet(string sourceType, string sourceId, out OctopusImportPriorMapping mapping)
        => Mappings.TryGetValue(Key(sourceType, sourceId), out mapping);

    /// <summary>
    /// The one normalisation every source-map key uses: trimmed, export suffix stripped (<see cref="OctopusImportSourceIds"/>),
    /// upper-case invariant. Writes and lookups both go through it, so file and API imports of one resource share a row.
    /// </summary>
    public static string Normalize(string value) => OctopusImportSourceIds.StripExportSuffix(value?.Trim())?.ToUpperInvariant() ?? string.Empty;

    public static (string Type, string Id) Key(string sourceType, string sourceId) => (Normalize(sourceType), Normalize(sourceId));

    private static Dictionary<(string Type, string Id), OctopusImportPriorMapping> Normalize(IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> mappings)
        => (mappings ?? new Dictionary<(string Type, string Id), OctopusImportPriorMapping>()).ToDictionary(pair => Key(pair.Key.Type, pair.Key.Id), pair => pair.Value);
}
