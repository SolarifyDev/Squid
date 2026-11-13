using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The persistent source map: the space lock serialises confirmations, the reloaded prior mappings are the only trusted
/// "imported before", and every mapping this confirmation made is recorded in the same transaction.
/// </summary>
public sealed partial class OctopusImportConfirmationOrchestrator
{
    private async Task<OctopusImportPriorMappings> LockSpaceAndLoadPriorMappingsAsync(int spaceId, CancellationToken ct)
    {
        if (_sourceMapDataProvider == null) return OctopusImportPriorMappings.Empty;

        await _sourceMapDataProvider.AcquireSpaceLockAsync(spaceId, ct).ConfigureAwait(false);

        return await _sourceMapDataProvider.LoadAsync(spaceId, ct).ConfigureAwait(false);
    }

    private async Task PersistSourceMapAsync(ConfirmationExecutionContext execution, CancellationToken ct)
    {
        if (_sourceMapDataProvider == null) return;

        var rows = BuildSourceMapRows(execution);

        if (rows.Count == 0) return;

        await _sourceMapDataProvider.UpsertAsync(rows, execution.Request.SessionId, ct).ConfigureAwait(false);
    }

    private static List<OctopusImportSourceMapRow> BuildSourceMapRows(ConfirmationExecutionContext execution)
    {
        var rows = execution.IdMap.Mappings.Select(mapping => SourceMapRow(execution, mapping)).ToList();

        AddSpaceRow(rows, execution, OctopusImportSourceMapQueries.InstallationSourceType, execution.Request.Graph.InstallationId);
        AddSpaceRow(rows, execution, OctopusImportSourceMapQueries.ExportSourceSourceType, execution.Request.Graph.ExportSourceId);

        return rows;
    }

    // A row describing where the space's import came from (installation, export fingerprint); it points at the space itself.
    private static void AddSpaceRow(List<OctopusImportSourceMapRow> rows, ConfirmationExecutionContext execution, string sourceType, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        rows.Add(new OctopusImportSourceMapRow(execution.DestinationSpaceId, sourceType, sourceId, execution.DestinationSpaceId, OctopusImportSourceMapQueries.ReusedOutcome));
    }

    private static OctopusImportSourceMapRow SourceMapRow(ConfirmationExecutionContext execution, OctopusImportIdMappingDto mapping)
    {
        var (fingerprint, detailFingerprint) = Fingerprints(execution, mapping);

        return new OctopusImportSourceMapRow(execution.DestinationSpaceId, mapping.SourceType, mapping.SourceId, mapping.DestinationId, mapping.OutcomeState.ToString(), fingerprint, detailFingerprint) { DetailsCleared = IsDetailsCleared(execution, mapping) };
    }

    // A history unit re-created without details: the details its stored fingerprint describes no longer exist.
    private static bool IsDetailsCleared(ConfirmationExecutionContext execution, OctopusImportIdMappingDto mapping)
        => mapping.SourceType == nameof(OctopusResourceKind.Deployment) && execution.HistoryDetailsCleared.Contains(mapping.SourceId);

    // Only what this confirmation wrote is fingerprinted; a reused row records none, so the stored fingerprints are kept.
    private static (string Fingerprint, string DetailFingerprint) Fingerprints(ConfirmationExecutionContext execution, OctopusImportIdMappingDto mapping)
    {
        if (execution.OutcomeOf(mapping.SourceId) is not (OctopusImportResourceOutcomeState.Created or OctopusImportResourceOutcomeState.Updated)) return (null, null);

        if (!Enum.TryParse<OctopusResourceKind>(mapping.SourceType, out var kind)) return (null, null);

        return kind switch
        {
            OctopusResourceKind.Deployment => execution.HistoryFingerprints.GetValueOrDefault(mapping.SourceId),
            OctopusResourceKind.ServerTask => (null, null),
            OctopusResourceKind.Release => (OctopusImportFingerprint.ForRelease(execution.FindResource(mapping.SourceId, kind)?.GetSource<OctopusReleaseDto>()), null),
            _ => (OctopusImportFingerprint.ForConfig(execution.FindResource(mapping.SourceId, kind)?.GetSource<OctopusDocumentDto>()), null)
        };
    }
}
