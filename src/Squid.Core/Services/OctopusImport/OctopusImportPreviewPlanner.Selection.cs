using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The session's project selection. A resource outside it (<see cref="OctopusImportReferenceScope.IsOutsideSelection"/>) is skipped
/// before any other decision — before history, "already imported", fills, choices, the whole-space scope rule and conflicts — so a
/// project that was not chosen is never written, filled, renamed or offered a choice, and its history is left as it is. Without a
/// selection nothing here applies.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private static OctopusImportResourceResultDto OutsideSelection(OctopusImportResourceResultDto result, OctopusResourceNode resource, PreviewPlanningContext context)
    {
        result.PreviewAction = OctopusImportPreviewAction.Skip;
        result.OutcomeState = OctopusImportResourceOutcomeState.Skipped;
        result.Diagnostics.Add(OutsideSelectionDiagnostic(resource, context));

        return result;
    }

    private static OctopusImportDiagnosticDto OutsideSelectionDiagnostic(OctopusResourceNode resource, PreviewPlanningContext context)
    {
        if (resource.Kind == OctopusResourceKind.Project) return Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ProjectNotChosen, OctopusImportPreviewDiagnosticCodes.ProjectNotChosenMessage, resource);

        if (!string.IsNullOrWhiteSpace(resource.OwnerProjectId)) return Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ProjectNotChosen, $"Belongs to Octopus project '{context.FindNode(resource.OwnerProjectId)?.Name ?? resource.OwnerProjectId}', which was not chosen for this import.", resource);

        // A task document need not name its project: one no chosen deployment runs belongs to a project that was not chosen.
        if (IsHistoryKind(resource.Kind)) return Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ProjectNotChosen, OctopusImportPreviewDiagnosticCodes.HistoryOfProjectNotChosenMessage, resource);

        return Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects, OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjectsMessage, resource);
    }

    // One line per kind for the shared resources no chosen project uses, and one line counting the projects not chosen.
    private static IEnumerable<OctopusImportDiagnosticDto> SelectionDiagnostics(IReadOnlyList<OctopusImportResourceResultDto> resources)
    {
        var unused = resources.Where(r => HasCode(r, OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects));
        var notChosen = resources.Count(IsNotChosenProject);

        var grouped = GroupByKind(unused, OctopusImportPreviewDiagnosticCodes.ResourcesNotUsedByChosenProjects, "are not imported because no chosen project uses them", OctopusImportCompatibilitySeverity.Info);

        return notChosen == 0 ? grouped : grouped.Append(NotChosenProjectsDiagnostic(notChosen));
    }

    private static OctopusImportDiagnosticDto NotChosenProjectsDiagnostic(int count)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Info, Code = OctopusImportPreviewDiagnosticCodes.ProjectsNotChosen, ResourceType = nameof(OctopusResourceKind.Project), Message = $"{count} Octopus project(s) were not chosen and are not imported." });

    private static bool IsNotChosenProject(OctopusImportResourceResultDto resource)
        => resource.SourceType == nameof(OctopusResourceKind.Project) && HasCode(resource, OctopusImportPreviewDiagnosticCodes.ProjectNotChosen);

    private static bool HasCode(OctopusImportResourceResultDto resource, string code) => resource.Diagnostics.Any(d => d.Code == code);
}
