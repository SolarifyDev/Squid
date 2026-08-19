using Squid.Core.Services.OctopusImport.Octopus;
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

public class OctopusImportPreviewValidator : IOctopusImportPreviewValidator
{
    private static readonly HashSet<OctopusResourceKind> ReusableSharedResourceKinds =
    [
        OctopusResourceKind.ProjectGroup,
        OctopusResourceKind.Environment,
        OctopusResourceKind.Lifecycle,
        OctopusResourceKind.Feed,
        OctopusResourceKind.Team,
        OctopusResourceKind.Machine,
        OctopusResourceKind.Account
    ];

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

        ValidateReferences(graph, selectedResources, allCurrentResources, result);
        ValidateReuse(conflictsBySourceId, previewResources, previewPlan.GeneratedAt, result);

        return result;
    }

    private static void ValidateReferences(
        OctopusResourceGraph graph,
        IReadOnlyDictionary<string, OctopusResourceNode> selectedResources,
        IReadOnlyDictionary<string, OctopusResourceNode> allCurrentResources,
        OctopusImportValidationResultDto result)
    {
        var machineRoles = graph.References
            .Where(r => r.FromKind == OctopusResourceKind.Machine && r.ReferenceKind == OctopusResourceReferenceKind.TargetRole)
            .Select(r => r.ToSourceId)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in graph.References.OrderBy(r => r.FromSourceId, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ReferenceKind).ThenBy(r => r.ToSourceId, StringComparer.OrdinalIgnoreCase))
        {
            if (!selectedResources.TryGetValue(reference.FromSourceId, out var source))
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

            if (reference.ReferenceKind == OctopusResourceReferenceKind.Account)
            {
                if (!allCurrentResources.ContainsKey(reference.ToSourceId))
                    AddReferenceDiagnostic(
                        result,
                        source,
                        OctopusImportPreviewDiagnosticCodes.MissingAccount,
                        $"Octopus account '{reference.ToSourceId}' is referenced but is missing from the current import graph.");

                continue;
            }

            if (reference.IsRequired && !selectedResources.ContainsKey(reference.ToSourceId))
            {
                AddReferenceDiagnostic(
                    result,
                    source,
                    OctopusImportPreviewDiagnosticCodes.UnresolvedReference,
                    $"Required Octopus {reference.ReferenceKind} reference '{reference.ToSourceId}' cannot be resolved in the selected current-configuration import plan.");
            }
        }
    }

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
                result.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
                {
                    Severity = OctopusImportCompatibilitySeverity.Blocker,
                    Code = OctopusImportPreviewDiagnosticCodes.StalePreviewPlan,
                    Message = $"Destination {match.Destination.Kind} '{match.Destination.Name}' was modified after the Octopus import preview was generated.",
                    SourceId = previewResource.SourceId,
                    ResourceType = previewResource.SourceType,
                    ResourceName = previewResource.SourceName
                }));
            }
        }
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
