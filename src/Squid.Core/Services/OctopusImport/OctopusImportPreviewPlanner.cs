using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportPreviewPlanner : IScopedDependency
{
    OctopusImportPreviewPlanDto BuildPreviewPlan(
        OctopusImportDependencyPlan dependencyPlan,
        OctopusImportConflictDiscoveryResult conflicts);
}

public partial class OctopusImportPreviewPlanner : IOctopusImportPreviewPlanner
{
    private readonly IOctopusExportSecretsAccessor _secretsAccessor;

    public OctopusImportPreviewPlanner(IOctopusExportSecretsAccessor secretsAccessor = null)
    {
        _secretsAccessor = secretsAccessor;
    }

    private OctopusExportSecrets Secrets => _secretsAccessor?.Secrets ?? OctopusExportSecrets.None;

    public OctopusImportPreviewPlanDto BuildPreviewPlan(
        OctopusImportDependencyPlan dependencyPlan,
        OctopusImportConflictDiscoveryResult conflicts)
    {
        ArgumentNullException.ThrowIfNull(dependencyPlan);
        ArgumentNullException.ThrowIfNull(conflicts);

        var nodes = dependencyPlan.OrderedResources
            .Concat(dependencyPlan.OutOfScopeResources)
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => OctopusResourceMetadata.For(r.Kind).Rank)
            .ThenBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var context = new PreviewPlanningContext(dependencyPlan, conflicts, nodes);
        var resources = BuildResourcePreviews(nodes, context);

        AddVariableDiagnostics(nodes, resources);

        var diagnostics = dependencyPlan.Diagnostics
            .Select(MapDependencyDiagnostic)
            .Concat(OctopusImportExportSecretDiagnostics.Describe(Secrets))
            .Concat(InstallationDiagnostics(conflicts))
            .ToList();

        return new OctopusImportPreviewPlanDto
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = resources,
            RequiredInputs = resources
                .SelectMany(r => r.RequiredInputs)
                .ToList(),
            Diagnostics = diagnostics
        };
    }

    // Rank order puts every owner before what it owns (project before channel, process before step,
    // variable set before variable, release before deployment before task), so each decision can read
    // the decisions already made for its owners.
    private List<OctopusImportResourceResultDto> BuildResourcePreviews(IReadOnlyList<OctopusResourceNode> nodes, PreviewPlanningContext context)
    {
        var resources = new List<OctopusImportResourceResultDto>();

        foreach (var resource in nodes)
            resources.Add(context.Record(BuildResourcePreview(resource, context)));

        return resources;
    }

    private static IEnumerable<OctopusImportDiagnosticDto> InstallationDiagnostics(OctopusImportConflictDiscoveryResult conflicts)
    {
        var mismatch = OctopusImportPriorMappingPolicy.InstallationMismatch(conflicts.HistoryInstallationId, conflicts.PriorMappings);

        return mismatch == null ? [] : [mismatch];
    }

    private OctopusImportResourceResultDto BuildResourcePreview(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        var result = new OctopusImportResourceResultDto
        {
            SourceId = resource.SourceId,
            SourceType = resource.Kind.ToString(),
            SourceName = resource.Name,
            OwnerProjectSourceId = resource.OwnerProjectId,
            OutcomeState = OctopusImportResourceOutcomeState.Pending
        };

        if (context.IsBlocked(resource.SourceId))
        {
            result.PreviewAction = OctopusImportPreviewAction.Blocked;
            result.OutcomeState = OctopusImportResourceOutcomeState.Blocked;
            result.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportPreviewDiagnosticCodes.ResourceBlockedByDependencyPlan,
                "This Octopus resource is blocked by a dependency-plan diagnostic.",
                resource));
            return result;
        }

        if (IsHistoryKind(resource.Kind))
        {
            ApplyDecision(result, DecideHistory(resource, context));
            return result;
        }

        if (resource.IsHistorical || OctopusResourceMetadata.For(resource.Kind).IsPreviewOutOfScope)
        {
            result.PreviewAction = OctopusImportPreviewAction.Skip;
            result.OutcomeState = OctopusImportResourceOutcomeState.Skipped;
            result.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Info,
                OctopusImportPreviewDiagnosticCodes.ResourceOutOfScope,
                $"Octopus {resource.Kind} resource is outside the initial current-configuration import scope.",
                resource));
            AddManualConfigurationDiagnostics(result, resource);
            return result;
        }

        if (OctopusResourceMetadata.For(resource.Kind).IsUnsupported)
        {
            result.PreviewAction = OctopusImportPreviewAction.Unsupported;
            result.OutcomeState = OctopusImportResourceOutcomeState.Unsupported;
            result.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportPreviewDiagnosticCodes.ResourceUnsupported,
                $"Octopus {resource.Kind} resources are detected but are not imported by the current preview planner.",
                resource));
            AddManualConfigurationDiagnostics(result, resource);
            return result;
        }

        if (OctopusImportPriorMappingPolicy.Decide(resource, context.Prior, context.PreviewActionOf) is { } priorDecision)
        {
            ApplyDecision(result, priorDecision);
            return result;
        }

        if (!context.TryGetConflict(resource.SourceId, out var conflict))
        {
            result.PreviewAction = OctopusImportPreviewAction.Create;
            AddTeamDiagnostics(result, resource);
            AddManualConfigurationDiagnostics(result, resource);
            return result;
        }

        ApplyConflictAction(result, resource, conflict);
        AddTeamDiagnostics(result, resource);
        AddManualConfigurationDiagnostics(result, resource);
        return result;
    }

    // Runs after every resource has its preview action, because whether a variable is written depends on
    // the action chosen for the library variable set that owns it.
    private void AddVariableDiagnostics(IReadOnlyList<OctopusResourceNode> nodes, IReadOnlyList<OctopusImportResourceResultDto> previews)
    {
        var nodesById = nodes.ToDictionary(n => n.SourceId, StringComparer.OrdinalIgnoreCase);
        var previewsById = previews.ToDictionary(p => p.SourceId, StringComparer.OrdinalIgnoreCase);

        OctopusResourceNode FindNode(string sourceId) => nodesById.GetValueOrDefault(sourceId);
        OctopusImportPreviewAction? PreviewAction(string sourceId) => previewsById.GetValueOrDefault(sourceId)?.PreviewAction;

        foreach (var resource in nodes.Where(n => !n.IsHistorical && n.Kind == OctopusResourceKind.Variable))
        {
            if (PreviewAction(resource.SourceId) == OctopusImportPreviewAction.AlreadyImported) continue;

            if (OctopusImportVariableOwnership.IsWrittenByImport(resource, FindNode, PreviewAction))
                AddRequiredInputs(previewsById[resource.SourceId], resource);
            else
                previewsById[resource.SourceId].Diagnostics.Add(Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ReusedLibraryVariableKept, "This variable belongs to a library variable set that is reused as-is; the destination keeps its existing value and this source value is not imported.", resource));
        }

        foreach (var shadow in OctopusImportVariablePrecedence.FindProjectVariablesShadowedByLibraries(nodes, set => OctopusImportVariableOwnership.IsWrittenByImport(set, FindNode, PreviewAction)))
            previewsById[shadow.ProjectVariable.SourceId].Diagnostics.Add(Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.ProjectVariableShadowedByLibraryVariable, ShadowMessage(shadow), shadow.ProjectVariable));
    }

    private static string ShadowMessage(OctopusImportShadowedProjectVariable shadow)
        => shadow.LibraryReused
            ? $"Project '{shadow.ProjectName}' defines variable '{shadow.VariableName}' and includes library variable set '{shadow.LibraryName}', which is reused as-is. The exported library defines the same name with an equally specific scope; the destination library's existing variables decide at runtime: if it still defines that name, Squid uses the library value where both apply, whereas Octopus used the project value. Check the destination library, and rename or re-scope one of them if the project value must win."
            : $"Project '{shadow.ProjectName}' defines variable '{shadow.VariableName}' and its included library variable set '{shadow.LibraryName}' defines the same name with an equally specific scope. Where both apply, Squid uses the library value, whereas Octopus used the project value. Rename or re-scope one of them if the project value must win.";

    private static void AddTeamDiagnostics(OctopusImportResourceResultDto result, OctopusResourceNode resource)
    {
        if (resource.Kind != OctopusResourceKind.Team) return;

        if (result.PreviewAction == OctopusImportPreviewAction.Create && OctopusBuiltInTeams.TryGetSquidEquivalent(resource.SourceId, out var squidTeamName))
        {
            BlockMissingBuiltInTeamEquivalent(result, resource, squidTeamName);
            return;
        }

        if (!OctopusBuiltInTeams.IsBuiltInWithoutEquivalent(resource.SourceId)) return;

        result.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportPreviewDiagnosticCodes.BuiltInTeamWithoutEquivalent,
            $"Octopus built-in team '{resource.Name}' has no Squid equivalent; it is imported as a regular team in this space with no members and no roles. Add members (or rely on the administrator approval override) before manual interventions assigned to it can be actioned.",
            resource));
    }

    // Never fall back to creating a namesake: a built-in Octopus team means "the system's built-in team",
    // so creating a lookalike would silently change who can approve.
    private static void BlockMissingBuiltInTeamEquivalent(OctopusImportResourceResultDto result, OctopusResourceNode resource, string squidTeamName)
    {
        result.PreviewAction = OctopusImportPreviewAction.Blocked;
        result.OutcomeState = OctopusImportResourceOutcomeState.Blocked;
        result.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportPreviewDiagnosticCodes.BuiltInTeamEquivalentMissing,
            $"Octopus built-in team '{resource.Name}' maps to the Squid built-in team '{squidTeamName}', which does not exist in the destination. Restore the Squid built-in team (it is seeded at server start-up) before importing.",
            resource));
    }

    private static void AddReusedTeamWarning(OctopusImportResourceResultDto result, OctopusResourceNode resource, OctopusImportDestinationResource destination)
    {
        if (IsEveryUserTeam(destination))
        {
            AddEveryUserTeamWarning(result, resource, destination);
            return;
        }

        result.DestinationMemberCount = destination.MemberCount ?? 0;
        result.DestinationMemberFingerprint = destination.MemberFingerprint;

        var scope = destination.SpaceId == 0 ? "system-wide" : $"space {destination.SpaceId}";

        result.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportPreviewDiagnosticCodes.ReusedTeamGrantsApproval,
            $"Existing team '{destination.Name}' ({scope}, id {destination.Id}) with {result.DestinationMemberCount} member(s) will be reused for Octopus team '{resource.Name}'. Its current members will be able to approve manual interventions assigned to that team.",
            resource));
    }

    // Squid adds every new user to the built-in Everyone team, so its membership is "all users", not a snapshot:
    // nothing is pinned for confirmation (a user created after the preview must not make the plan stale).
    private static void AddEveryUserTeamWarning(OctopusImportResourceResultDto result, OctopusResourceNode resource, OctopusImportDestinationResource destination)
    {
        result.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportPreviewDiagnosticCodes.ReusedTeamGrantsApproval,
            $"Octopus team '{resource.Name}' maps to the Squid built-in team '{destination.Name}' (id {destination.Id}), which every user belongs to: every user will be able to approve manual interventions assigned to that team.",
            resource));
    }

    private static bool IsEveryUserTeam(OctopusImportDestinationResource destination)
        => destination.IsBuiltIn && destination.SpaceId == 0 && destination.Name == BuiltInTeamNames.Everyone;

    private void AddRequiredInputs(
        OctopusImportResourceResultDto result,
        OctopusResourceNode resource)
    {
        var variable = resource.GetSource<OctopusVariableDto>();
        if (!OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable))
            return;

        if (OctopusImportSensitiveValues.IsResolvable(variable, Secrets))
        {
            result.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Info,
                OctopusImportPreviewDiagnosticCodes.SensitiveVariableValueDecrypted,
                "The sensitive variable value was decrypted with the export password and will be imported encrypted at rest.",
                resource));
            return;
        }

        result.RequiredInputs.Add(OctopusImportRequiredInputBuilder.ForSensitiveVariable(resource.SourceId, variable));
        result.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing,
            OctopusImportPreviewDiagnosticCodes.SensitiveValueUnresolvedMessage,
            resource));
    }

    private static void ApplyConflictAction(
        OctopusImportResourceResultDto result,
        OctopusResourceNode resource,
        OctopusImportResourceConflict conflict)
    {
        if (resource.Kind == OctopusResourceKind.Project)
        {
            result.PreviewAction = OctopusImportPreviewAction.RenameRequired;
            result.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject,
                "A project with the same name or slug already exists in the destination space. Project imports are never silently merged.",
                resource));
            return;
        }

        if (conflict.Matches.Count != 1)
        {
            result.PreviewAction = OctopusImportPreviewAction.RenameRequired;
            result.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportPreviewDiagnosticCodes.RenameRequiredForAmbiguousConflict,
                $"Found {conflict.Matches.Count} destination resources with matching identity. Select a destination resource or rename the Octopus resource before importing.",
                resource));
            return;
        }

        var match = conflict.Matches[0];
        result.PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        result.DestinationId = match.Destination.Id;
        result.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Info,
            OctopusImportPreviewDiagnosticCodes.ReuseExistingResource,
            $"A compatible destination {resource.Kind} identity was found by {match.MatchKind}.",
            resource));

        if (resource.Kind == OctopusResourceKind.Team)
            AddReusedTeamWarning(result, resource, match.Destination);
    }

    private static OctopusImportDiagnosticDto MapDependencyDiagnostic(OctopusInputExtractionDiagnostic diagnostic)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = diagnostic.Severity,
            Code = string.IsNullOrWhiteSpace(diagnostic.Code)
                ? OctopusImportPreviewDiagnosticCodes.DependencyPlanBlocker
                : diagnostic.Code,
            Message = diagnostic.Message,
            SourceId = diagnostic.SourceId,
            ResourceType = diagnostic.DocumentKind?.ToString()
        });

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceNode resource)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = resource.Kind.ToString(),
            SourceId = resource.SourceId,
            ResourceName = resource.Name
        });

    private bool IsResolvedFeedCredentialDiagnostic(OctopusResourceNode resource, OctopusImportDiagnosticDto diagnostic)
        => resource.Kind == OctopusResourceKind.Feed
           && diagnostic.Code == OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted
           && OctopusImportFeedCredentials.IsResolvable(resource.GetSource<OctopusFeedDto>(), Secrets);

    private void AddManualConfigurationDiagnostics(
        OctopusImportResourceResultDto result,
        OctopusResourceNode resource)
    {
        foreach (var diagnostic in OctopusImportManualConfiguration.BuildRequiredConfigurationDiagnostics(resource))
        {
            if (IsResolvedFeedCredentialDiagnostic(resource, diagnostic)) continue;

            result.Diagnostics.Add(diagnostic);
        }
    }

}
