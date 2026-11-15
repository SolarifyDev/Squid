using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Secrets an earlier (API) import left missing, seen from a resource that was imported before: fill it from the export file, keep
/// what Squid holds, settle it when Octopus has no value either, or say why it stays missing. Decided after the prior-mapping
/// decision, so "already imported" never hides a fill; decided by <see cref="OctopusImportSecretFills"/>, so confirmation does the same.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private void ApplySecretFill(OctopusImportResourceResultDto result, OctopusResourceNode resource, PreviewPlanningContext context)
    {
        // A variable no longer in Squid (Skip: not in the earlier import) still names why its missing secret cannot be filled.
        if (result.PreviewAction is not (OctopusImportPreviewAction.AlreadyImported or OctopusImportPreviewAction.Skip) || !OctopusImportSecretFills.IsFillableKind(resource.Kind)) return;

        var candidate = OctopusImportSecretFills.Candidate(resource, context.Prior, Secrets, context.VariableKeyCounts);

        if (candidate == null) return;

        candidate = candidate with { Destination = OctopusImportFillDestinations.Of(candidate, context.Conflicts.FillDestinations) };
        result.FillDestination = candidate.Destination;

        var destination = OctopusImportSecretFills.DestinationState(candidate, context.Conflicts.SecretSlots);
        var (severity, code, message) = DescribeSecretFill(candidate, destination, context);

        if (OctopusImportSecretFills.WouldFill(candidate, destination) && context.FillEvidence.State != OctopusImportFillEvidenceState.Unproven)
        {
            result.PreviewAction = OctopusImportPreviewAction.FillMissingSecret;
            result.Changes = OctopusImportPreviewChanges.ForSecretFill(candidate.Kind, SquidFeedUsername(candidate, context), candidate.FillUsername);
        }

        context.SecretFillTally[code] = context.SecretFillTally.GetValueOrDefault(code) + 1;
        result.Diagnostics.Add(Diagnostic(severity, code, message, resource));
    }

    private static string SquidFeedUsername(OctopusImportSecretFillCandidate candidate, PreviewPlanningContext context)
        => candidate.Kind == OctopusResourceKind.Feed ? context.Conflicts.SecretSlots.Feeds.FirstOrDefault(f => f.FeedId == candidate.DestinationId)?.Username : null;

    private (OctopusImportCompatibilitySeverity Severity, string Code, string Message) DescribeSecretFill(OctopusImportSecretFillCandidate candidate, SecretSlotState destination, PreviewPlanningContext context)
    {
        if (destination != SecretSlotState.Empty) return (OctopusImportCompatibilitySeverity.Info, OctopusImportSecretFillMessages.KeptCode(destination), OctopusImportSecretFillMessages.Kept(candidate, destination));

        return candidate.Source switch
        {
            OctopusImportSecretFillSource.Ambiguous => (OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.SecretFillAmbiguous, OctopusImportSecretFillMessages.SourceAmbiguous(candidate)),
            OctopusImportSecretFillSource.StillMissing => (OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.SecretStillMissing, OctopusImportSecretFillMessages.StillMissing(candidate, Secrets.IsWithheldByServer)),
            _ => DescribeFileAction(candidate, context)
        };
    }

    // The file would write (or settle) the secret: only with installation evidence.
    private static (OctopusImportCompatibilitySeverity Severity, string Code, string Message) DescribeFileAction(OctopusImportSecretFillCandidate candidate, PreviewPlanningContext context)
    {
        context.SecretFillsNeedingEvidence++;

        var evidence = context.FillEvidence.State;

        if (evidence == OctopusImportFillEvidenceState.Unproven) return (OctopusImportCompatibilitySeverity.Blocker, OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven, OctopusImportSecretFillMessages.Unproven(candidate, context.FillEvidence));

        var pairingRequired = evidence == OctopusImportFillEvidenceState.PairingRequired;

        return candidate.Source == OctopusImportSecretFillSource.EmptyInOctopus
            ? (OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.SecretEmptyInOctopus, OctopusImportSecretFillMessages.EmptyInOctopus(candidate, pairingRequired))
            : (OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.SecretFillFromExport, OctopusImportSecretFillMessages.Fill(candidate, pairingRequired));
    }

    private static IEnumerable<OctopusImportDiagnosticDto> SecretFillSummary(PreviewPlanningContext context)
    {
        if (context.SecretFillTally.Count == 0) yield break;

        int Count(string code) => context.SecretFillTally.GetValueOrDefault(code);

        yield return OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Info,
            Code = OctopusImportPreviewDiagnosticCodes.SecretFillsSummary,
            Message = $"Values left missing by an earlier import of this space: {Count(OctopusImportPreviewDiagnosticCodes.SecretFillFromExport)} filled from this export file, {Count(OctopusImportPreviewDiagnosticCodes.SecretSetInSquid)} already set in Squid (kept, never overwritten), {Count(OctopusImportPreviewDiagnosticCodes.SecretEmptyInOctopus)} empty in Octopus too (settled, nothing written), {Count(OctopusImportPreviewDiagnosticCodes.SecretStillMissing) + Count(OctopusImportPreviewDiagnosticCodes.SecretFillAmbiguous)} still missing, {Count(OctopusImportPreviewDiagnosticCodes.SecretFillTargetGone)} no longer in Squid, {Count(OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven)} refused (export file not proven to be from this installation)."
        });

        if (PairingRequired(context))
            yield return OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Warning, Code = OctopusImportPreviewDiagnosticCodes.SecretFillPairingRequired, Message = OctopusImportSecretFillMessages.PairingRequired(context.FillEvidence.MatchingStructureIds, context.Prior.InstallationId, context.Conflicts.InstallationOrigin, context.SecretFillsNeedingEvidence) });
    }

    private static OctopusImportFillPairingDto FillPairing(PreviewPlanningContext context)
    {
        if (!PairingRequired(context)) return null;

        return new OctopusImportFillPairingDto
        {
            Required = true,
            InstallationId = context.Prior.InstallationId,
            ServerHost = context.Conflicts.InstallationOrigin?.ServerHost,
            OctopusSpaceId = context.Conflicts.InstallationOrigin?.OctopusSpaceId,
            OctopusVersion = context.Conflicts.InstallationOrigin?.OctopusVersion,
            FileExportSourceId = context.Conflicts.ExportSourceId,
            RecordedExportSourceId = OctopusImportFillEvidences.RecordedExportSourceId(context.Prior),
            MatchingStructureIds = context.FillEvidence.MatchingStructureIds
        };
    }

    private static bool PairingRequired(PreviewPlanningContext context)
        => context.SecretFillsNeedingEvidence > 0 && context.FillEvidence.State == OctopusImportFillEvidenceState.PairingRequired;
}
