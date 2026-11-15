using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Secrets an earlier import left <c>Missing</c>, at confirmation. A fill is re-decided here from the server's own data (rows reloaded
/// under the space lock, the session's export secrets, the installation evidence), never taken from the posted plan, and is written by
/// the owning service's compare-and-set: only a value that is STILL empty is filled, so a value set in Squid at any moment — before
/// the preview, between preview and confirm, or concurrently — is never overwritten. Every outcome settles the row for good:
/// <c>Filled</c> (written, or empty in Octopus too) or <c>Superseded</c> (Squid holds a value, or the variable is gone).
/// </summary>
public sealed partial class OctopusImportConfirmationOrchestrator
{
    private async Task FillMissingSecretAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportResourceResultDto preview, CancellationToken ct)
    {
        ReuseImportedResource(execution, resource, preview);

        var candidate = FillCandidate(execution, resource);

        if (candidate?.Source != OctopusImportSecretFillSource.Fillable || FillEvidence(execution).State != OctopusImportFillEvidenceState.Proven)
            throw StalePlan(execution, resource, OctopusImportSecretFillMessages.NotBacked);

        var state = await WriteFillAsync(execution, resource, candidate, ct).ConfigureAwait(false);

        candidate = NamedAsPreviewed(candidate, preview);
        preview.FillDestination = candidate.Destination;

        RecordFill(execution, resource, candidate, state);
    }

    private async Task<SecretSlotState> WriteFillAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportSecretFillCandidate candidate, CancellationToken ct)
    {
        if (candidate.Kind == OctopusResourceKind.Feed)
        {
            var credentials = OctopusImportFeedCredentials.Resolve(resource.GetSource<OctopusFeedDto>(), Secrets);

            return await RequireFeedService().FillMissingCredentialsAsync(candidate.DestinationId, credentials.Username, credentials.Password, ct).ConfigureAwait(false);
        }

        if (!OctopusImportSensitiveValues.TryResolve(resource.GetSource<OctopusVariableDto>(), Secrets, out var value))
            throw StalePlan(execution, resource, OctopusImportSecretFillMessages.NotBacked);

        return await RequireVariableService().FillEmptySensitiveValueAsync(candidate.DestinationId, candidate.Identity, value, ct).ConfigureAwait(false);
    }

    private static void RecordFill(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportSecretFillCandidate candidate, SecretSlotState state)
    {
        if (state == SecretSlotState.Filled)
        {
            Settle(execution, candidate, OctopusImportSourceMapQueries.FilledOutcome);
            execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.SecretFilled);
            execution.AddDiagnostic(resource, Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.SecretFillFromExport, $"The missing {OctopusImportSecretFillMessages.What(candidate.Kind)} was filled from the export file into {OctopusImportSecretFillMessages.Where(candidate)}.", resource));
            return;
        }

        var outcome = OctopusImportSecretFills.SettledWithoutWrite(candidate, state, OctopusImportFillEvidenceState.Proven);

        if (outcome != null) Settle(execution, candidate, outcome);

        execution.AddDiagnostic(resource, Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportSecretFillMessages.KeptCode(state), OctopusImportSecretFillMessages.Kept(candidate, state), resource));
    }

    // Resources this confirmation did not fill but that speak for a missing secret: Squid now holds a value (or the variable is gone)
    // → Superseded; a proven export file says Octopus has no value either → Filled. Anything else stays Missing.
    private async Task SettleMissingSecretsAsync(ConfirmationExecutionContext execution, CancellationToken ct)
    {
        var candidates = execution.CurrentResources.Where(r => OctopusImportSecretFills.IsFillableKind(r.Kind) && execution.OutcomeOf(r.SourceId) == OctopusImportResourceOutcomeState.Reused)
            .Select(r => FillCandidate(execution, r))
            .Where(c => c != null && !execution.SettledSecretRows.ContainsKey((c.RowSourceType, OctopusImportPriorMappings.Normalize(c.RowKey))))
            .ToList();

        if (candidates.Count == 0) return;

        var slots = await LoadSecretSlotsAsync(candidates, ct).ConfigureAwait(false);
        var evidence = FillEvidence(execution).State;

        foreach (var candidate in candidates)
        {
            var outcome = OctopusImportSecretFills.SettledWithoutWrite(candidate, OctopusImportSecretFills.DestinationState(candidate, slots), evidence);

            if (outcome != null) Settle(execution, candidate, outcome);
        }
    }

    private async Task<OctopusImportSecretSlots> LoadSecretSlotsAsync(IReadOnlyList<OctopusImportSecretFillCandidate> candidates, CancellationToken ct)
    {
        var variableSetIds = candidates.Where(c => c.Kind == OctopusResourceKind.Variable).Select(c => c.DestinationId).Distinct().ToList();
        var feedIds = candidates.Where(c => c.Kind == OctopusResourceKind.Feed).Select(c => c.DestinationId).Distinct().ToList();

        var variables = variableSetIds.Count == 0 ? [] : await RequireVariableService().GetSensitiveVariableSlotsAsync(variableSetIds, ct).ConfigureAwait(false);
        var feeds = feedIds.Count == 0 ? [] : await RequireFeedService().GetCredentialSlotsAsync(feedIds, ct).ConfigureAwait(false);

        return new OctopusImportSecretSlots(variables, feeds);
    }

    // Only the name for the result comes from the plan, and only when it points at the very row this confirmation wrote.
    private static OctopusImportSecretFillCandidate NamedAsPreviewed(OctopusImportSecretFillCandidate candidate, OctopusImportResourceResultDto preview)
        => preview?.FillDestination == null ? candidate : candidate with { Destination = OctopusImportFillDestinations.Of(candidate, [preview.FillDestination]) };

    private OctopusImportSecretFillCandidate FillCandidate(ConfirmationExecutionContext execution, OctopusResourceNode resource)
        => OctopusImportSecretFills.Candidate(resource, execution.Prior, Secrets, execution.VariableKeyCounts);

    // The pairing confirmation is the user's: it travels in the validated plan. Everything else is the server's own data.
    private static OctopusImportFillEvidence FillEvidence(ConfirmationExecutionContext execution)
        => OctopusImportFillEvidences.Evaluate(execution.Request.Graph.InstallationId, execution.Request.Graph.ExportSourceId, execution.Request.Graph.Resources, execution.Prior, execution.Request.PreviewPlan.FillPairing?.Confirmed == true);

    private static void Settle(ConfirmationExecutionContext execution, OctopusImportSecretFillCandidate candidate, string outcome)
        => execution.SettledSecretRows[(candidate.RowSourceType, OctopusImportPriorMappings.Normalize(candidate.RowKey))] = (candidate.DestinationId, outcome);

    private Squid.Core.Services.Deployments.Variables.IVariableService RequireVariableService()
        => _variableService ?? throw new InvalidOperationException("Filling missing Octopus secrets needs the variable service.");

    private Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedService RequireFeedService()
        => _externalFeedService ?? throw new InvalidOperationException("Filling missing Octopus secrets needs the external feed service.");
}
