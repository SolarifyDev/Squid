namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The source-map rows of one space, keyed by normalised (source type, source id). <see cref="Mappings"/> holds only live rows
/// (the loader drops rows whose destination no longer exists, so every entry resolves); <see cref="IsDeletedInSquid"/> names the
/// recorded rows of the choosable child kinds whose destination is gone.
/// </summary>
public sealed class OctopusImportPriorMappings
{
    public static OctopusImportPriorMappings Empty { get; } = new(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(), null);

    private readonly IReadOnlySet<(string Type, string Id)> _deletedKeys;

    public OctopusImportPriorMappings(IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> mappings, string installationId, IReadOnlyList<string> exportSourceIds = null, IReadOnlySet<(string Type, string Id)> deletedKeys = null)
    {
        Mappings = Normalize(mappings);
        InstallationId = installationId;
        ExportSourceIds = exportSourceIds ?? [];
        _deletedKeys = NormalizeDeleted(deletedKeys, Mappings);
    }

    public IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> Mappings { get; }

    /// <summary>The Octopus installation this space's history was fetched from, or null when none is recorded.</summary>
    public string InstallationId { get; }

    /// <summary>The export-source fingerprints (export id suffixes) recorded for this space, oldest first; empty when no suffixed export was imported.</summary>
    public IReadOnlyList<string> ExportSourceIds { get; }

    public bool TryGet(string sourceType, string sourceId, out OctopusImportPriorMapping mapping)
        => Mappings.TryGetValue(Key(sourceType, sourceId), out mapping);

    /// <summary>An earlier import recorded this resource, and the Squid row it created is gone (deleted in Squid since). A live row never is.</summary>
    public bool IsDeletedInSquid(string sourceType, string sourceId) => _deletedKeys.Contains(Key(sourceType, sourceId));

    /// <summary>
    /// The one normalisation every source-map key uses: trimmed, export suffix stripped (<see cref="OctopusImportSourceIds"/>),
    /// upper-case invariant. Writes and lookups both go through it, so file and API imports of one resource share a row.
    /// </summary>
    public static string Normalize(string value) => OctopusImportSourceIds.StripExportSuffix(value?.Trim())?.ToUpperInvariant() ?? string.Empty;

    public static (string Type, string Id) Key(string sourceType, string sourceId) => (Normalize(sourceType), Normalize(sourceId));

    private static HashSet<(string Type, string Id)> NormalizeDeleted(IReadOnlySet<(string Type, string Id)> deletedKeys, IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> live)
        => (deletedKeys ?? new HashSet<(string Type, string Id)>()).Select(key => Key(key.Type, key.Id)).Where(key => !live.ContainsKey(key)).ToHashSet();

    private static Dictionary<(string Type, string Id), OctopusImportPriorMapping> Normalize(IReadOnlyDictionary<(string Type, string Id), OctopusImportPriorMapping> mappings)
        => (mappings ?? new Dictionary<(string Type, string Id), OctopusImportPriorMapping>()).ToDictionary(pair => Key(pair.Key.Type, pair.Key.Id), pair => pair.Value);
}
