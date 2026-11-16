using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportPreviewValidator : IScopedDependency
{
    OctopusImportValidationResultDto Validate(
        OctopusResourceGraph graph,
        OctopusImportDependencyPlan dependencyPlan,
        OctopusImportConflictDiscoveryResult conflicts,
        OctopusImportPreviewPlanDto previewPlan);
}

public partial class OctopusImportPreviewValidator : IOctopusImportPreviewValidator
{
    private static readonly HashSet<OctopusResourceKind> ReusableSharedResourceKinds =
    [
        OctopusResourceKind.ProjectGroup,
        OctopusResourceKind.Environment,
        OctopusResourceKind.Lifecycle,
        OctopusResourceKind.Feed,
        OctopusResourceKind.Team,
        OctopusResourceKind.Machine,
        OctopusResourceKind.Account,
        OctopusResourceKind.LibraryVariableSet,
        // A channel added in Octopus after its project's import, linked to the one Squid channel of that name in the project.
        OctopusResourceKind.Channel
    ];

    private readonly IOctopusExportSecretsAccessor _secretsAccessor;
    private readonly IOctopusImportPreviewPlanner _previewPlanner;

    public OctopusImportPreviewValidator(IOctopusExportSecretsAccessor secretsAccessor = null, IOctopusImportPreviewPlanner previewPlanner = null)
    {
        _secretsAccessor = secretsAccessor;
        _previewPlanner = previewPlanner ?? new OctopusImportPreviewPlanner(secretsAccessor);
    }

    private OctopusExportSecrets Secrets => _secretsAccessor?.Secrets ?? OctopusExportSecrets.None;

    public OctopusImportValidationResultDto Validate(
        OctopusResourceGraph graph,
        OctopusImportDependencyPlan dependencyPlan,
        OctopusImportConflictDiscoveryResult conflicts,
        OctopusImportPreviewPlanDto previewPlan)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dependencyPlan);
        ArgumentNullException.ThrowIfNull(conflicts);
        ArgumentNullException.ThrowIfNull(previewPlan);

        var result = new OctopusImportValidationResultDto();
        var selectedResources = dependencyPlan.OrderedResources
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(r => r.SourceId, StringComparer.OrdinalIgnoreCase);
        var allCurrentResources = graph.Resources
            .Where(r => !r.IsHistorical)
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(r => r.SourceId, StringComparer.OrdinalIgnoreCase);
        var previewResources = previewPlan.Resources
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var conflictsBySourceId = conflicts.Conflicts
            .GroupBy(c => c.Source.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var plan = new ValidationPlan(selectedResources, allCurrentResources, previewResources, conflicts.PriorMappings);

        ValidateReferences(graph, plan, result);
        ValidateVariableScopes(plan, result);
        ValidateReleasePackageFeeds(graph, plan, result);
        ValidateOrigin(graph, conflicts, previewPlan, result);
        ValidateProjectConflicts(conflictsBySourceId, previewResources, result);
        ValidateReuse(conflictsBySourceId, previewResources, previewPlan.GeneratedAt, result);
        ValidateChoices(dependencyPlan, conflicts, previewPlan, result);
        // Server-built inputs come first so their severity (not a client-posted IsRequired) wins the de-duplication.
        result.RequiredInputs = BuildRequiredInputs(plan)
            .Concat(previewPlan.RequiredInputs)
            .Concat(previewResources.Values.SelectMany(r => r.RequiredInputs))
            .Where(input => !IsSatisfiedSensitiveInput(input, plan))
            .GroupBy(input => input.InputKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        ValidateRequiredInputs(result);
        ValidateSensitiveResolutionFreshness(previewResources, selectedResources, result);
        ValidateSecretFills(graph, conflicts, previewPlan, plan, result);
        result.Diagnostics.AddRange(OctopusImportExportSecretDiagnostics.Describe(Secrets));

        return result;
    }

    // The preview shows the origin check too, but the plan-level diagnostics are client-posted; these are server-owned. Another
    // installation always refuses; a source linking to earlier rows must be proven, or its pairing confirmed by the user and backed
    // by matching process ids (a posted confirmation alone proves nothing: the evidence is re-evaluated here).
    private static void ValidateOrigin(OctopusResourceGraph graph, OctopusImportConflictDiscoveryResult conflicts, OctopusImportPreviewPlanDto previewPlan, OctopusImportValidationResultDto result)
    {
        var prior = conflicts.PriorMappings;
        var mismatch = OctopusImportPriorMappingPolicy.InstallationMismatch(conflicts.HistoryInstallationId, prior, conflicts.InstallationOrigin, conflicts.SourceOrigin);

        if (mismatch != null)
        {
            result.Diagnostics.Add(mismatch);
            return;
        }

        if (!OctopusImportFillEvidences.NeedsOriginProof(conflicts.HistoryInstallationId, conflicts.ExportSourceId, graph.Resources, prior)) return;

        var evidence = OctopusImportFillEvidences.Evaluate(conflicts.HistoryInstallationId, conflicts.ExportSourceId, graph.Resources, prior, previewPlan.FillPairing?.Confirmed == true);

        if (OctopusImportFillEvidences.RefusesLinking(evidence))
            result.Diagnostics.Add(PlanDiagnostic(OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, OctopusImportOriginMessages.Refused(evidence, OctopusImportOriginMessages.Source(conflicts.HistoryInstallationId, conflicts.ExportSourceId, conflicts.SourceOrigin), OctopusImportOriginMessages.Recorded(prior, conflicts.InstallationOrigin), OtherOriginCreators(graph, conflicts))));
        else if (evidence.State == OctopusImportFillEvidenceState.PairingRequired)
            result.Diagnostics.Add(PlanDiagnostic(OctopusImportPreviewDiagnosticCodes.ImportOriginPairingUnconfirmed, OctopusImportOriginMessages.PairingUnconfirmed));
    }

    private static IReadOnlyList<string> OtherOriginCreators(OctopusResourceGraph graph, OctopusImportConflictDiscoveryResult conflicts)
        => OctopusImportOriginMessages.Creators(OctopusImportFillEvidences.OtherOriginCreators(conflicts.HistoryInstallationId, conflicts.ExportSourceId, graph.Resources, conflicts.PriorMappings), conflicts.CreatorOrigins);

    private static OctopusImportDiagnosticDto PlanDiagnostic(string code, string message)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = code, Message = message });

    private static void ValidateReleasePackageFeeds(OctopusResourceGraph graph, ValidationPlan plan, OctopusImportValidationResultDto result)
    {
        var selectedResources = plan.Selected;
        var previewResources = plan.Previews;

        foreach (var releaseResource in selectedResources.Values.Where(resource => resource.Kind == OctopusResourceKind.Release))
        {
            if (!previewResources.TryGetValue(releaseResource.SourceId, out var releasePreview)
                || releasePreview.PreviewAction != OctopusImportPreviewAction.Create)
            {
                continue;
            }

            var release = releaseResource.GetSource<OctopusReleaseDto>();
            if (release == null)
                continue;

            foreach (var selectedPackage in (release.SelectedPackages ?? []).Where(package =>
                         !string.IsNullOrWhiteSpace(package.ActionName)
                         && !string.IsNullOrWhiteSpace(package.Version)))
            {
                var sourceFeedId = OctopusImportReleasePackageFeedResolver.GetSourceFeedId(
                    graph,
                    selectedResources.Values,
                    release,
                    selectedPackage);
                if (OctopusImportReleasePackageFeedResolver.HasPlannedDestination(sourceFeedId, previewResources, plan.Prior))
                    continue;

                if (OctopusBuiltInFeeds.IsBuiltInId(sourceFeedId))
                    AddReferenceDiagnostic(result, releaseResource, OctopusImportPreviewDiagnosticCodes.BuiltInFeedReferenced, OctopusBuiltInFeeds.ReferencedMessage);
                else
                    AddReferenceDiagnostic(
                        result,
                        releaseResource,
                        OctopusImportConfirmationDiagnosticCodes.MissingReleasePackageFeedMapping,
                        $"Octopus release package selection for action '{selectedPackage.ActionName}' references a package feed that cannot be mapped to a destination Squid feed.");
            }
        }
    }

    // A sensitive input is satisfied when the export password decrypts it, or when the variable is never
    // written because its library is reused as-is (the destination keeps its own value).
    private bool IsSatisfiedSensitiveInput(OctopusImportRequiredInputDto input, ValidationPlan plan)
    {
        if (input.Kind != OctopusImportRequiredInputKind.SensitiveVariableValue) return false;
        if (string.IsNullOrWhiteSpace(input.SourceId) || !plan.Selected.TryGetValue(input.SourceId, out var resource)) return false;
        if (resource.Kind != OctopusResourceKind.Variable) return false;

        return !plan.IsWrittenByImport(resource) || OctopusImportSensitiveValues.IsResolvable(resource.GetSource<OctopusVariableDto>(), Secrets);
    }

    private IEnumerable<OctopusImportRequiredInputDto> BuildRequiredInputs(ValidationPlan plan)
    {
        foreach (var resource in plan.Selected.Values.Where(resource => resource.Kind == OctopusResourceKind.Variable && plan.IsWrittenByImport(resource)))
        {
            var variable = resource.GetSource<OctopusVariableDto>();
            if (variable != null && OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable))
                yield return OctopusImportRequiredInputBuilder.ForSensitiveVariable(resource.SourceId, variable, Secrets);
        }
    }

    // A preview built while the value could not be decrypted still carries its required-input blocker even
    // though the value now decrypts (the export secrets became readable in between). Say so explicitly instead
    // of rejecting the plan with a validation result that shows nothing wrong.
    private void ValidateSensitiveResolutionFreshness(IReadOnlyDictionary<string, OctopusImportResourceResultDto> previewResources, IReadOnlyDictionary<string, OctopusResourceNode> selectedResources, OctopusImportValidationResultDto result)
    {
        foreach (var preview in previewResources.Values.Where(HasUnresolvedSensitiveBlocker))
        {
            if (!selectedResources.TryGetValue(preview.SourceId, out var resource) || resource.Kind != OctopusResourceKind.Variable) continue;
            if (!OctopusImportSensitiveValues.IsResolvable(resource.GetSource<OctopusVariableDto>(), Secrets)) continue;

            result.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Blocker,
                Code = OctopusImportPreviewDiagnosticCodes.SensitiveResolutionChanged,
                Message = OctopusImportPreviewDiagnosticCodes.SensitiveResolutionChangedMessage,
                SourceId = resource.SourceId,
                ResourceType = resource.Kind.ToString(),
                ResourceName = resource.Name
            }));
        }
    }

    // Server-owned: the client's plan can neither invent a fill (no recorded missing value, or nothing the file can supply) nor skip
    // the installation evidence. A value set in Squid since the preview is not a plan error: confirmation's compare-and-set keeps it
    // and settles the row as Superseded. A file that would write or settle a missing value must be proven to come from this
    // space's Octopus installation — or the user must have confirmed the pairing the preview showed.
    private void ValidateSecretFills(OctopusResourceGraph graph, OctopusImportConflictDiscoveryResult conflicts, OctopusImportPreviewPlanDto previewPlan, ValidationPlan plan, OctopusImportValidationResultDto result)
    {
        var keyCounts = OctopusImportSecretFills.VariableKeyCounts(plan.Current.Values, plan.Prior);
        var evidence = OctopusImportFillEvidences.Evaluate(conflicts.HistoryInstallationId, conflicts.ExportSourceId, graph.Resources, plan.Prior, previewPlan.FillPairing?.Confirmed == true);
        var pairingNeeded = false;

        foreach (var resource in plan.Current.Values.Where(r => OctopusImportSecretFills.IsFillableKind(r.Kind)))
        {
            var candidate = OctopusImportSecretFills.Candidate(resource, plan.Prior, Secrets, keyCounts);
            var destination = candidate == null ? SecretSlotState.NotFound : OctopusImportSecretFills.DestinationState(candidate, conflicts.SecretSlots);

            if (plan.PreviewAction(resource.SourceId) == OctopusImportPreviewAction.FillMissingSecret && candidate?.Source != OctopusImportSecretFillSource.Fillable)
                result.Diagnostics.Add(ResourceDiagnostic(resource, OctopusImportPreviewDiagnosticCodes.SecretFillNotBacked, OctopusImportSecretFillMessages.NotBacked));

            if (!ActsOnMissingSecret(candidate, destination)) continue;

            if (evidence.State == OctopusImportFillEvidenceState.Unproven)
                result.Diagnostics.Add(ResourceDiagnostic(resource, OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven, OctopusImportSecretFillMessages.Unproven(candidate, evidence)));

            pairingNeeded |= evidence.State == OctopusImportFillEvidenceState.PairingRequired;
        }

        if (pairingNeeded)
            result.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = OctopusImportPreviewDiagnosticCodes.SecretFillPairingUnconfirmed, Message = OctopusImportSecretFillMessages.PairingUnconfirmed }));
    }

    // The file writes the value, or settles it as empty in Octopus.
    private static bool ActsOnMissingSecret(OctopusImportSecretFillCandidate candidate, SecretSlotState destination)
        => OctopusImportSecretFills.WouldFill(candidate, destination) || (candidate?.Source == OctopusImportSecretFillSource.EmptyInOctopus && destination == SecretSlotState.Empty);

    private static OctopusImportDiagnosticDto ResourceDiagnostic(OctopusResourceNode resource, string code, string message)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = code, Message = message, SourceId = resource.SourceId, ResourceType = resource.Kind.ToString(), ResourceName = resource.Name });

    private static bool HasUnresolvedSensitiveBlocker(OctopusImportResourceResultDto preview)
        => preview.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker && d.Code == OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing);

    private static void ValidateRequiredInputs(OctopusImportValidationResultDto result)
    {
        foreach (var input in result.RequiredInputs.Where(input => input.IsRequired))
        {
            result.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Blocker,
                Code = OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing,
                Message = OctopusImportPreviewDiagnosticCodes.SensitiveValueUnresolvedMessage,
                SourceId = input.SourceId,
                ResourceType = input.SourceType,
                ResourceName = input.Name
            }));
        }
    }

    private static void ValidateProjectConflicts(
        IReadOnlyDictionary<string, OctopusImportResourceConflict> conflictsBySourceId,
        IReadOnlyDictionary<string, OctopusImportResourceResultDto> previewResources,
        OctopusImportValidationResultDto result)
    {
        foreach (var conflict in conflictsBySourceId.Values.Where(c => c.Source.Kind == OctopusResourceKind.Project))
        {
            if (previewResources.TryGetValue(conflict.Source.SourceId, out var previewResource)
                && !ClaimsProjectName(previewResource.PreviewAction))
            {
                continue;
            }

            AddReferenceDiagnostic(
                result,
                conflict.Source,
                OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject,
                "A project with the same name or slug already exists in the destination space. Project imports are never silently merged.");
        }
    }

    // Only a project this import writes (or a forged reuse) can collide by name. An already-imported
    // project IS the destination row the conflict found, so it never needs a rename.
    private static bool ClaimsProjectName(OctopusImportPreviewAction action)
        => OctopusImportPreviewActions.WritesDestination(action) || action == OctopusImportPreviewAction.ReuseExisting;

    // Predicts confirmation's scope mapping with the SAME rule the variable mapper uses: a scope target
    // resolves only if confirmation will have created or reused it before the variable set is written.
    private static void ValidateVariableScopes(ValidationPlan plan, OctopusImportValidationResultDto result)
    {
        foreach (var resource in plan.Selected.Values.Where(r => r.Kind == OctopusResourceKind.Variable && !r.IsHistorical && plan.IsWrittenByImport(r)))
        {
            var variable = resource.GetSource<OctopusVariableDto>();
            var writerKind = OctopusImportVariableScopeRule.WriterKind(plan.FindResource(resource.ParentSourceId)?.GetSource<OctopusVariableSetDto>());

            foreach (var resolution in OctopusImportVariableScopeRule.ResolveAll(variable?.Scope, (sourceId, kind) => plan.IsImportedBefore(sourceId, kind, writerKind)).Where(r => r.Blocks))
                AddReferenceDiagnostic(result, resource, OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope, UnmappableScopeMessage(variable, resolution));
        }
    }

    private static string UnmappableScopeMessage(OctopusVariableDto variable, OctopusImportScopeResolution resolution)
    {
        var reason = resolution.Outcome == OctopusImportScopeOutcome.UnsupportedType
            ? $"Squid variables cannot be scoped by {resolution.ScopeKey}"
            : $"that {resolution.SourceKind} is not created or reused by this import before the variable is written";

        return $"Octopus variable '{variable.Name}' is scoped to {resolution.ScopeKey} '{resolution.ScopeValue}', but {reason}. Squid never widens a variable's scope, so the import is blocked until the scope is removed in Octopus or the scoped resource is imported.";
    }

    private static void ValidateReferences(OctopusResourceGraph graph, ValidationPlan plan, OctopusImportValidationResultDto result)
    {
        var selectedResources = plan.Selected;
        var allCurrentResources = plan.Current;
        var machineRoles = graph.References
            .Where(r => r.FromKind == OctopusResourceKind.Machine && r.ReferenceKind == OctopusResourceReferenceKind.TargetRole)
            .Select(r => r.ToSourceId)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in graph.References.OrderBy(r => r.FromSourceId, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReferenceKind).ThenBy(r => r.ToSourceId, StringComparer.OrdinalIgnoreCase))
        {
            if (!selectedResources.TryGetValue(reference.FromSourceId, out var source) || !plan.IsWrittenByImport(source))
                continue;

            if (reference.ReferenceKind == OctopusResourceReferenceKind.TargetRole)
            {
                if (!machineRoles.Contains(reference.ToSourceId))
                    AddReferenceDiagnostic(
                        result,
                        source,
                        OctopusImportPreviewDiagnosticCodes.MissingTargetRole,
                        $"Octopus target role '{reference.ToSourceId}' is referenced but no exported deployment target declares that role.");

                continue;
            }

            if (reference.ReferenceKind == OctopusResourceReferenceKind.Machine)
            {
                if (!allCurrentResources.ContainsKey(reference.ToSourceId))
                    AddReferenceDiagnostic(
                        result,
                        source,
                        OctopusImportPreviewDiagnosticCodes.MissingMachine,
                        $"Octopus machine '{reference.ToSourceId}' is referenced but is missing from the current import graph.");

                continue;
            }

            if (reference.ReferenceKind == OctopusResourceReferenceKind.Team)
            {
                // Dropping a responsible team would WIDEN who can approve the manual intervention,
                // so an approval team missing from the export blocks before any write is attempted.
                if (!allCurrentResources.ContainsKey(reference.ToSourceId) && !plan.IsPriorMapped(reference))
                    AddReferenceDiagnostic(
                        result,
                        source,
                        OctopusImportPreviewDiagnosticCodes.MissingResponsibleTeam,
                        $"Octopus responsible team '{reference.ToSourceId}' is referenced by a manual intervention but is missing from the export. Re-export including the team so approval rights are preserved.");

                continue;
            }

            if (reference.ReferenceKind == OctopusResourceReferenceKind.Account)
            {
                if (!allCurrentResources.ContainsKey(reference.ToSourceId) && !plan.IsPriorMapped(reference))
                    AddReferenceDiagnostic(
                        result,
                        source,
                        OctopusImportPreviewDiagnosticCodes.MissingAccount,
                        $"Octopus account '{reference.ToSourceId}' is referenced but is missing from the current import graph.");

                continue;
            }

            if (IsMissingBuiltInFeed(reference, plan))
            {
                AddReferenceDiagnostic(result, source, OctopusImportPreviewDiagnosticCodes.BuiltInFeedReferenced, OctopusBuiltInFeeds.ReferencedMessage);
                continue;
            }

            if (reference.IsRequired && !selectedResources.ContainsKey(reference.ToSourceId) && !plan.IsPriorMapped(reference))
            {
                AddReferenceDiagnostic(
                    result,
                    source,
                    OctopusImportPreviewDiagnosticCodes.UnresolvedReference,
                    $"Required Octopus {reference.ReferenceKind} reference '{reference.ToSourceId}' cannot be resolved in the selected current-configuration import plan.");
            }
        }
    }

    // The REST source never carries Octopus's built-in repository, so a step that uses it is named for what it is.
    private static bool IsMissingBuiltInFeed(OctopusResourceReference reference, ValidationPlan plan)
        => reference.ReferenceKind == OctopusResourceReferenceKind.Feed && OctopusBuiltInFeeds.IsBuiltInId(reference.ToSourceId) && !plan.Selected.ContainsKey(reference.ToSourceId) && !plan.IsPriorMapped(reference);

    private static void ValidateReuse(
        IReadOnlyDictionary<string, OctopusImportResourceConflict> conflictsBySourceId,
        IReadOnlyDictionary<string, OctopusImportResourceResultDto> previewResources,
        DateTimeOffset previewGeneratedAt,
        OctopusImportValidationResultDto result)
    {
        foreach (var previewResource in previewResources.Values.Where(r => r.PreviewAction == OctopusImportPreviewAction.ReuseExisting))
        {
            if (!conflictsBySourceId.TryGetValue(previewResource.SourceId, out var conflict))
            {
                AddIncompatibleReuseDiagnostic(result, previewResource);
                continue;
            }

            if (!IsValidReuse(previewResource, conflict, out var match))
            {
                AddIncompatibleReuseDiagnostic(result, previewResource);
                continue;
            }

            if (previewGeneratedAt != default && match.Destination.LastModifiedDate > previewGeneratedAt)
            {
                AddStalePreviewDiagnostic(result, previewResource, $"Destination {match.Destination.Kind} '{match.Destination.Name}' was modified after the Octopus import preview was generated.");
                continue;
            }

            if (HasMembershipChanged(previewResource, match))
                AddStalePreviewDiagnostic(result, previewResource, $"Destination team '{match.Destination.Name}' membership changed after the preview was generated (it had {previewResource.DestinationMemberCount} member(s) and now has {match.Destination.MemberCount ?? 0}); review who will be able to approve before importing again.");
        }
    }

    // Team membership lives in its own table and does not touch the team row's LastModifiedDate, so the
    // membership the preview warned about is compared explicitly: reusing a team grants approval rights.
    // The count catches additions/removals; the fingerprint also catches a member swapped for another.
    // Null preview values mean "nothing pinned" (other kinds, the Everyone team, plans predating the field).
    private static bool HasMembershipChanged(OctopusImportResourceResultDto previewResource, OctopusImportDestinationMatch match)
        => HasMemberCountChanged(previewResource, match) || HasMemberFingerprintChanged(previewResource, match);

    private static bool HasMemberCountChanged(OctopusImportResourceResultDto previewResource, OctopusImportDestinationMatch match)
        => previewResource.DestinationMemberCount.HasValue && previewResource.DestinationMemberCount.Value != (match.Destination.MemberCount ?? 0);

    private static bool HasMemberFingerprintChanged(OctopusImportResourceResultDto previewResource, OctopusImportDestinationMatch match)
        => previewResource.DestinationMemberFingerprint != null && !string.Equals(previewResource.DestinationMemberFingerprint, match.Destination.MemberFingerprint, StringComparison.Ordinal);

    private static void AddStalePreviewDiagnostic(OctopusImportValidationResultDto result, OctopusImportResourceResultDto previewResource, string message)
    {
        result.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Blocker,
            Code = OctopusImportPreviewDiagnosticCodes.StalePreviewPlan,
            Message = message,
            SourceId = previewResource.SourceId,
            ResourceType = previewResource.SourceType,
            ResourceName = previewResource.SourceName
        }));
    }

    private static bool IsValidReuse(
        OctopusImportResourceResultDto previewResource,
        OctopusImportResourceConflict conflict,
        out OctopusImportDestinationMatch match)
    {
        match = null;

        if (previewResource.DestinationId == null || conflict.Matches.Count != 1)
            return false;

        match = conflict.Matches[0];

        return match.Destination.Id == previewResource.DestinationId.Value
            && string.Equals(conflict.Source.Kind.ToString(), previewResource.SourceType, StringComparison.OrdinalIgnoreCase)
            && match.Destination.Kind == conflict.Source.Kind
            && ReusableSharedResourceKinds.Contains(conflict.Source.Kind);
    }

    private static void AddIncompatibleReuseDiagnostic(
        OctopusImportValidationResultDto result,
        OctopusImportResourceResultDto previewResource)
    {
        result.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Blocker,
            Code = OctopusImportPreviewDiagnosticCodes.IncompatibleSharedResourceReuse,
            Message = "Preview selected reuse for a resource that no longer has exactly one compatible destination match.",
            SourceId = previewResource.SourceId,
            ResourceType = previewResource.SourceType,
            ResourceName = previewResource.SourceName
        }));
    }

    private sealed record ValidationPlan(IReadOnlyDictionary<string, OctopusResourceNode> Selected, IReadOnlyDictionary<string, OctopusResourceNode> Current, IReadOnlyDictionary<string, OctopusImportResourceResultDto> Previews, OctopusImportPriorMappings Prior)
    {
        public OctopusResourceNode FindResource(string sourceId)
            => sourceId != null && Current.TryGetValue(sourceId, out var resource) ? resource : null;

        public OctopusImportPreviewAction? PreviewAction(string sourceId)
            => sourceId != null && Previews.TryGetValue(sourceId, out var preview) ? preview.PreviewAction : null;

        // A skipped resource is not written either (history whose owner is not imported, a default channel
        // that is never remapped), so nothing it references can block the import.
        public bool IsWrittenByImport(OctopusResourceNode resource)
            => PreviewAction(resource?.SourceId) != OctopusImportPreviewAction.Skip && OctopusImportVariableOwnership.IsWrittenByImport(resource, FindResource, PreviewAction);

        // A target recorded by an earlier import maps to its Squid row even when this source does not carry it.
        public bool IsPriorMapped(OctopusResourceReference reference)
            => reference.ToKind != null && Prior.TryGet(reference.ToKind.Value.ToString(), reference.ToSourceId, out _);

        public bool IsImportedBefore(string sourceId, OctopusResourceKind kind, OctopusResourceKind writerKind)
            => (IsResolvedByThisPlan(sourceId, kind) || Prior.TryGet(kind.ToString(), sourceId, out _)) && OctopusImportVariableScopeRule.IsImportedBefore(kind, writerKind);

        private bool IsResolvedByThisPlan(string sourceId, OctopusResourceKind kind)
            => Selected.TryGetValue(sourceId, out var target)
               && target.Kind == kind
               && PreviewAction(sourceId) is { } action
               && OctopusImportPreviewActions.ResolvesToDestination(action);
    }

    private static void AddReferenceDiagnostic(
        OctopusImportValidationResultDto result,
        OctopusResourceNode source,
        string code,
        string message)
    {
        result.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Blocker,
            Code = code,
            Message = message,
            SourceId = source.SourceId,
            ResourceType = source.Kind.ToString(),
            ResourceName = source.Name
        }));
    }
}
