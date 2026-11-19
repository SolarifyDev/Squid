using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Config-as-Code projects (<see cref="OctopusImportReferenceScope.IsConfigAsCode"/>). Their process and settings live in git, so neither
/// source carries them: such a project and everything it owns is skipped with its own reason right after the selection check — before
/// blockers, history, "already imported", fills, choices, the whole-space scope rule and conflicts — so it never blocks the import, is
/// never half-imported, and a project imported before its conversion is left exactly as it is in Squid.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private static OctopusImportResourceResultDto ConfigAsCodeSkip(OctopusImportResourceResultDto result, OctopusResourceNode resource, PreviewPlanningContext context)
    {
        result.PreviewAction = OctopusImportPreviewAction.Skip;
        result.OutcomeState = OctopusImportResourceOutcomeState.Skipped;
        result.Diagnostics.Add(Diagnostic(OctopusImportCompatibilitySeverity.Info, OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported, ConfigAsCodeMessage(resource, context), resource));

        return result;
    }

    private static string ConfigAsCodeMessage(OctopusResourceNode resource, PreviewPlanningContext context)
        => resource.Kind == OctopusResourceKind.Project ? OctopusImportPreviewDiagnosticCodes.ConfigAsCodeProjectMessage : $"Belongs to Octopus project '{context.FindNode(resource.OwnerProjectId)?.Name ?? resource.OwnerProjectId}', which is version-controlled (Config as Code) and not imported.";

    // One plan line counting the Config-as-Code projects left out.
    private static IEnumerable<OctopusImportDiagnosticDto> ConfigAsCodeDiagnostics(IReadOnlyList<OctopusImportResourceResultDto> resources)
    {
        var count = resources.Count(OctopusImportPreviewSummaries.IsConfigAsCodeProject);

        if (count == 0) yield break;

        yield return OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Info, Code = OctopusImportPreviewDiagnosticCodes.ConfigAsCodeProjectsNotImported, ResourceType = nameof(OctopusResourceKind.Project), Message = $"{count} Config-as-Code (version-controlled) Octopus project(s) are not imported: Squid does not import Config-as-Code projects." });
    }
}
