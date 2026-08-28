using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportPreviewPlanner : IScopedDependency
{
    OctopusImportPreviewPlanDto BuildPreviewPlan(
        OctopusImportDependencyPlan dependencyPlan,
        OctopusImportConflictDiscoveryResult conflicts);
}

public class OctopusImportPreviewPlanner : IOctopusImportPreviewPlanner
{
    public OctopusImportPreviewPlanDto BuildPreviewPlan(
        OctopusImportDependencyPlan dependencyPlan,
        OctopusImportConflictDiscoveryResult conflicts)
    {
        ArgumentNullException.ThrowIfNull(dependencyPlan);
        ArgumentNullException.ThrowIfNull(conflicts);

        var conflictsBySourceId = conflicts.Conflicts
            .GroupBy(c => c.Source.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var blockedSourceIds = dependencyPlan.Diagnostics
            .Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker && !string.IsNullOrWhiteSpace(d.SourceId))
            .Select(d => d.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resources = dependencyPlan.OrderedResources
            .Concat(dependencyPlan.OutOfScopeResources)
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => Rank(r.Kind))
            .ThenBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(resource => BuildResourcePreview(resource, conflictsBySourceId, blockedSourceIds))
            .ToList();

        var diagnostics = dependencyPlan.Diagnostics
            .Select(MapDependencyDiagnostic)
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

    private static OctopusImportResourceResultDto BuildResourcePreview(
        OctopusResourceNode resource,
        IReadOnlyDictionary<string, OctopusImportResourceConflict> conflictsBySourceId,
        IReadOnlySet<string> blockedSourceIds)
    {
        var result = new OctopusImportResourceResultDto
        {
            SourceId = resource.SourceId,
            SourceType = resource.Kind.ToString(),
            SourceName = resource.Name,
            OutcomeState = OctopusImportResourceOutcomeState.Pending
        };

        AddRequiredInputs(result, resource);

        if (blockedSourceIds.Contains(resource.SourceId))
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

        if (resource.IsHistorical || IsOutOfScope(resource.Kind))
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

        if (IsUnsupported(resource.Kind))
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

        if (!conflictsBySourceId.TryGetValue(resource.SourceId, out var conflict))
        {
            result.PreviewAction = OctopusImportPreviewAction.Create;
            AddManualConfigurationDiagnostics(result, resource);
            return result;
        }

        ApplyConflictAction(result, resource, conflict);
        AddManualConfigurationDiagnostics(result, resource);
        return result;
    }

    private static void AddRequiredInputs(
        OctopusImportResourceResultDto result,
        OctopusResourceNode resource)
    {
        if (resource.IsHistorical || resource.Kind != OctopusResourceKind.Variable)
            return;

        var variable = resource.GetSource<OctopusVariableDto>();
        if (!OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable))
            return;

        result.RequiredInputs.Add(OctopusImportRequiredInputBuilder.ForSensitiveVariable(resource.SourceId, variable));
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

    private static void AddManualConfigurationDiagnostics(
        OctopusImportResourceResultDto result,
        OctopusResourceNode resource)
    {
        foreach (var diagnostic in OctopusImportManualConfiguration.BuildRequiredConfigurationDiagnostics(resource))
            result.Diagnostics.Add(diagnostic);
    }

    private static bool IsOutOfScope(OctopusResourceKind kind)
        => kind is OctopusResourceKind.Release
            or OctopusResourceKind.Deployment
            or OctopusResourceKind.ServerTask
            or OctopusResourceKind.DeploymentProcessSnapshot
            or OctopusResourceKind.VariableSetSnapshot
            or OctopusResourceKind.WorkerPool;

    private static bool IsUnsupported(OctopusResourceKind kind)
        => kind is OctopusResourceKind.Unknown
            or OctopusResourceKind.ActionTemplate
            or OctopusResourceKind.Certificate;

    private static int Rank(OctopusResourceKind kind)
    {
        return kind switch
        {
            OctopusResourceKind.ProjectGroup => 10,
            OctopusResourceKind.Environment => 20,
            OctopusResourceKind.Lifecycle => 30,
            OctopusResourceKind.LifecyclePhase => 40,
            OctopusResourceKind.Feed => 50,
            OctopusResourceKind.Team => 60,
            OctopusResourceKind.Machine => 70,
            OctopusResourceKind.Account => 80,
            OctopusResourceKind.Certificate => 90,
            OctopusResourceKind.Project => 100,
            OctopusResourceKind.Channel => 110,
            OctopusResourceKind.DeploymentSettings => 120,
            OctopusResourceKind.VariableSet => 130,
            OctopusResourceKind.Variable => 140,
            OctopusResourceKind.DeploymentProcess => 150,
            OctopusResourceKind.DeploymentStep => 160,
            OctopusResourceKind.DeploymentAction => 170,
            OctopusResourceKind.DeploymentProcessSnapshot => 900,
            OctopusResourceKind.VariableSetSnapshot => 910,
            OctopusResourceKind.Release => 920,
            OctopusResourceKind.Deployment => 930,
            OctopusResourceKind.ServerTask => 940,
            OctopusResourceKind.WorkerPool => 950,
            _ => 1000
        };
    }
}
