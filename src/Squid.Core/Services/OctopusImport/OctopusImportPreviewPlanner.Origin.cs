using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The origin of the whole import. Ids like <c>Projects-1</c> repeat across Octopus servers, so a source that would link to rows an
/// import of another origin created must come from the Octopus the space was imported from (<see cref="OctopusImportFillEvidences"/>):
/// another installation, no process id in common, or rows a third origin created refuse the import; matching process ids ask the user
/// to confirm the pairing.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private static IEnumerable<OctopusImportDiagnosticDto> OriginDiagnostics(PreviewPlanningContext context)
    {
        var conflicts = context.Conflicts;
        var mismatch = OctopusImportPriorMappingPolicy.InstallationMismatch(conflicts.HistoryInstallationId, context.Prior, conflicts.InstallationOrigin, conflicts.SourceOrigin);

        if (mismatch != null) return [mismatch];

        if (!context.NeedsOriginProof) return [];

        if (OctopusImportFillEvidences.RefusesLinking(context.FillEvidence)) return [OriginDiagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, OctopusImportOriginMessages.Refused(context.FillEvidence, SourceText(context), RecordedText(context), context.OtherOriginCreators))];

        // A fill's own pairing warning already asks for the same confirmation.
        if (context.FillEvidence.State != OctopusImportFillEvidenceState.PairingRequired || FillsNeedPairing(context)) return [];

        return [OriginDiagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, OctopusImportOriginMessages.PairingRequired(context.FillEvidence.MatchingStructureIds, SourceText(context), RecordedText(context)))];
    }

    // The verdict the page shows. A file of another export fingerprint is not refused (it degrades, see the history diagnostics).
    private static OctopusImportOriginEvidenceDto OriginEvidence(PreviewPlanningContext context)
    {
        var anotherInstallation = OctopusImportFillEvidences.IsAnotherInstallation(context.Conflicts.HistoryInstallationId, context.Prior);

        if (!anotherInstallation && !context.NeedsOriginProof) return null;

        var evidence = context.FillEvidence;

        if (OctopusImportFillEvidences.IsAnotherExportSource(evidence)) return null;

        var creators = evidence.Basis == OctopusImportFillEvidences.OtherOriginRowsBasis ? context.OtherOriginCreators : null;

        return new OctopusImportOriginEvidenceDto { State = evidence.State, Basis = evidence.Basis, MatchingStructureIds = evidence.MatchingStructureIds, Source = SourceText(context), Recorded = RecordedText(context), Creators = creators?.Take(OctopusImportOriginMessages.NamedCreatorLimit).ToList(), MoreCreators = Math.Max(0, (creators?.Count ?? 0) - OctopusImportOriginMessages.NamedCreatorLimit) };
    }

    private static string SourceText(PreviewPlanningContext context)
        => OctopusImportOriginMessages.Source(context.Conflicts.HistoryInstallationId, context.Conflicts.ExportSourceId, context.Conflicts.SourceOrigin);

    private static string RecordedText(PreviewPlanningContext context) => OctopusImportOriginMessages.Recorded(context.Prior, context.Conflicts.InstallationOrigin);

    private static OctopusImportDiagnosticDto OriginDiagnostic(OctopusImportCompatibilitySeverity severity, string code, string message)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = severity, Code = code, Message = message });
}
