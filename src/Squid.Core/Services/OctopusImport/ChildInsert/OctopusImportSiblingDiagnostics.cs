using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.ChildInsert;

/// <summary>
/// A child is mapped together with its siblings (the mappers map a whole process or set), so the diagnostics of the siblings that are not
/// inserted are left out by their source ids. Everything else is kept, an unattributed diagnostic included: a blocker that might be the
/// child's own always blocks it.
/// </summary>
public static class OctopusImportSiblingDiagnostics
{
    public static IReadOnlyList<OctopusImportDiagnosticDto> Without(IEnumerable<OctopusImportDiagnosticDto> diagnostics, IReadOnlySet<string> foreignSourceIds)
        => (diagnostics ?? []).Where(d => string.IsNullOrWhiteSpace(d.SourceId) || !foreignSourceIds.Contains(d.SourceId)).ToList();

    public static bool HasBlocker(IEnumerable<OctopusImportDiagnosticDto> diagnostics) => diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);

    /// <summary>A step's ids (as Octopus gives it and as the graph names it) and the ids of its actions.</summary>
    public static IEnumerable<string> StepIds(OctopusDeploymentStepDto step, string stepSourceId)
        => new[] { step.Id, stepSourceId }.Concat((step.Actions ?? []).SelectMany((action, index) => new[] { action.Id, OctopusResourceGraphBuilder.BuildChildSourceId(stepSourceId, "action", action.Id, index) })).Where(id => !string.IsNullOrWhiteSpace(id));
}
