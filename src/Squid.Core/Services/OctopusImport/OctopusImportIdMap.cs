using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The Octopus → Squid id mappings of one confirmation. Session entries are the ones this confirmation
/// created or reused; the read-only prior layer holds what earlier confirmations recorded in the source
/// map. Lookups see both (session first), but only session entries are ever added, copied or reported.
/// </summary>
public sealed class OctopusImportIdMap
{
    private readonly Dictionary<OctopusImportIdMapKey, OctopusImportIdMappingDto> _mappings;
    private readonly OctopusImportPriorMappings _prior;

    public OctopusImportIdMap()
        : this([], OctopusImportPriorMappings.Empty)
    {
    }

    public OctopusImportIdMap(OctopusImportPriorMappings prior)
        : this([], prior ?? OctopusImportPriorMappings.Empty)
    {
    }

    private OctopusImportIdMap(IEnumerable<OctopusImportIdMappingDto> mappings, OctopusImportPriorMappings prior)
    {
        _mappings = new Dictionary<OctopusImportIdMapKey, OctopusImportIdMappingDto>();
        _prior = prior;

        foreach (var mapping in mappings)
            Add(mapping);
    }

    public IReadOnlyCollection<OctopusImportIdMappingDto> Mappings => _mappings.Values;

    public static OctopusImportIdMap FromSessionResult(OctopusImportSessionResultDto result)
        => new(result?.IdMappings ?? [], OctopusImportPriorMappings.Empty);

    public OctopusImportIdMappingDto AddCreated(OctopusResourceNode source, int destinationId, string destinationType = null)
        => Add(source, destinationId, OctopusImportResourceOutcomeState.Created, destinationType);

    public OctopusImportIdMappingDto AddReused(OctopusResourceNode source, int destinationId, string destinationType = null)
        => Add(source, destinationId, OctopusImportResourceOutcomeState.Reused, destinationType);

    public bool TryGetDestinationId(OctopusResourceNode source, out int destinationId)
        => TryGetDestinationId(source?.SourceId, source?.Kind.ToString(), out destinationId);

    public bool TryGetDestinationId(OctopusResourceReference reference, out int destinationId)
        => TryGetDestinationId(reference?.ToSourceId, reference?.ToKind?.ToString(), out destinationId);

    public bool TryGetDestinationId(string sourceId, string sourceType, out int destinationId)
    {
        if (_mappings.TryGetValue(OctopusImportIdMapKey.Create(sourceId, sourceType), out var mapping))
        {
            destinationId = mapping.DestinationId;
            return true;
        }

        return TryGetPriorDestinationId(sourceId, sourceType, out destinationId);
    }

    /// <summary>True only when THIS confirmation already mapped the resource; a prior-layer hit never counts, so it never suppresses a session write.</summary>
    public bool ContainsSessionMapping(OctopusResourceNode source)
        => source != null && _mappings.ContainsKey(OctopusImportIdMapKey.Create(source.SourceId, source.Kind.ToString()));

    private bool TryGetPriorDestinationId(string sourceId, string sourceType, out int destinationId)
    {
        destinationId = default;

        if (!_prior.TryGet(sourceType, sourceId, out var prior)) return false;

        destinationId = prior.DestinationId;
        return true;
    }

    public IReadOnlyList<OctopusImportIdMappingDto> ToDto()
    {
        return _mappings.Values
            .OrderBy(m => m.SourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CopyTo(OctopusImportSessionResultDto result)
    {
        ArgumentNullException.ThrowIfNull(result);

        result.IdMappings = ToDto().ToList();
    }

    private OctopusImportIdMappingDto Add(OctopusResourceNode source, int destinationId, OctopusImportResourceOutcomeState outcomeState, string destinationType)
    {
        ArgumentNullException.ThrowIfNull(source);

        var mapping = new OctopusImportIdMappingDto
        {
            SourceId = source.SourceId,
            SourceType = source.Kind.ToString(),
            SourceName = source.Name,
            DestinationType = destinationType ?? source.Kind.ToString(),
            DestinationId = destinationId,
            OutcomeState = outcomeState
        };

        Add(mapping);
        return mapping;
    }

    private void Add(OctopusImportIdMappingDto mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        Validate(mapping);

        var key = OctopusImportIdMapKey.Create(mapping.SourceId, mapping.SourceType);

        if (_mappings.ContainsKey(key))
            throw new InvalidOperationException($"Octopus import source '{mapping.SourceType}:{mapping.SourceId}' has already been mapped.");

        _mappings.Add(key, mapping);
    }

    private static void Validate(OctopusImportIdMappingDto mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.SourceId))
            throw new ArgumentException("Octopus import ID mappings require a source id.", nameof(mapping));

        if (string.IsNullOrWhiteSpace(mapping.SourceType))
            throw new ArgumentException("Octopus import ID mappings require a source type.", nameof(mapping));

        if (string.IsNullOrWhiteSpace(mapping.DestinationType))
            throw new ArgumentException("Octopus import ID mappings require a destination type.", nameof(mapping));

        if (mapping.DestinationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(mapping), mapping.DestinationId, "Octopus import ID mappings require a positive destination id.");

        if (mapping.OutcomeState is not (OctopusImportResourceOutcomeState.Created or OctopusImportResourceOutcomeState.Reused))
            throw new ArgumentOutOfRangeException(nameof(mapping), mapping.OutcomeState, "Octopus import ID mappings only support created or reused destination resources.");
    }

    private readonly record struct OctopusImportIdMapKey(string SourceId, string SourceType)
    {
        public static OctopusImportIdMapKey Create(string sourceId, string sourceType)
            => new(Normalize(sourceId), Normalize(sourceType));

        private static string Normalize(string value) => value?.Trim().ToUpperInvariant() ?? string.Empty;
    }
}
