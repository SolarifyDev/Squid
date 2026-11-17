using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
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

    // Validation ran before the space lock: another import may have recorded an origin (or rows) since. Re-checked against the locked
    // prior mappings with the same evidence, so a source of another Octopus never links to them; a forged pairing confirmation is
    // re-evaluated too (it proves nothing without matching process ids).
    private static void EnsureOriginStillProven(ConfirmationExecutionContext execution)
    {
        var graph = execution.Request.Graph;
        var anotherInstallation = OctopusImportFillEvidences.IsAnotherInstallation(graph.InstallationId, execution.Prior);

        if (!anotherInstallation && !OctopusImportFillEvidences.NeedsOriginProof(graph.InstallationId, graph.ExportSourceId, graph.Resources, execution.Prior)) return;

        var evidence = FillEvidence(execution);

        if (!OctopusImportFillEvidences.RefusesLinking(evidence) && evidence.State != OctopusImportFillEvidenceState.PairingRequired) return;

        var diagnostic = Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, OctopusImportOriginMessages.StillUnproven);

        execution.Result.Diagnostics.Add(diagnostic);

        throw new OctopusImportConfirmationException("The import's Octopus origin is not proven.");
    }

    private async Task PersistSourceMapAsync(ConfirmationExecutionContext execution, CancellationToken ct)
    {
        if (_sourceMapDataProvider == null) return;

        var rows = BuildSourceMapRows(execution);

        if (rows.Count == 0) return;

        await _sourceMapDataProvider.UpsertAsync(rows, execution.Request.SessionId, ct).ConfigureAwait(false);
    }

    private List<OctopusImportSourceMapRow> BuildSourceMapRows(ConfirmationExecutionContext execution)
    {
        var rows = execution.IdMap.Mappings.Select(mapping => SourceMapRow(execution, mapping)).Concat(SecretRows(execution)).ToList();

        if (MayRecordOrigin(execution))
            AddSpaceRow(rows, execution, OctopusImportSourceMapQueries.InstallationSourceType, execution.Request.Graph.InstallationId);

        if (MayPinExportSource(execution))
            AddSpaceRow(rows, execution, OctopusImportSourceMapQueries.ExportSourceSourceType, execution.Request.Graph.ExportSourceId);

        return rows.Select(row => WithCreatingOrigin(row, execution.Request.Graph)).ToList();
    }

    // Every row names the origin of this import; the store keeps it only on rows this import creates (or re-points).
    private static OctopusImportSourceMapRow WithCreatingOrigin(OctopusImportSourceMapRow row, OctopusResourceGraph graph)
        => row with { CreatedInstallationId = graph.InstallationId, CreatedExportSourceId = graph.ExportSourceId };

    // A recorded export fingerprint proves later files of it (links, history, secret fills), so it is recorded only from a file that is
    // the space's origin: the space's first export-file import, or a file whose pairing with the origin the user confirmed (backed by
    // matching process ids). The first one stays pinned; each confirmed one is added next to it as another export of the same Octopus,
    // so its next import is proven without asking. A file of an unknown fingerprint that needed no proof is never recorded.
    private static bool MayPinExportSource(ConfirmationExecutionContext execution)
        => !OctopusImportFillEvidences.IsKnownExportSource(execution.Request.Graph.ExportSourceId, execution.Prior) && MayRecordOrigin(execution);

    // A recorded origin proves every later import of that source by id for the rows attributed to it, so the source becomes one only
    // where it cannot vouch for rows another origin created: an empty space or one whose every row has a known creator and no recorded
    // origin, or a space whose origin it is proven to be (by id or a confirmed pairing). An import with no id in common needs no proof
    // to run, but it does not make its server the origin of another server's (or unknown) rows.
    private static bool MayRecordOrigin(ConfirmationExecutionContext execution)
        => IsOriginless(execution.Prior) || FillEvidence(execution).State == OctopusImportFillEvidenceState.Proven;

    private static bool IsOriginless(OctopusImportPriorMappings prior) => !OctopusImportFillEvidences.HasRecordedOrigin(prior) && !OctopusImportFillEvidences.HasRowsOfUnknownOrigin(prior);

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
