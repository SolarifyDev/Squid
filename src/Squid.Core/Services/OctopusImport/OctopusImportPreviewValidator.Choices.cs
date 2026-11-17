using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The rows the user decides about are the server's: the plan is built again from Squid as it is now, with the posted selection, and
/// every row that offers a choice in either plan must match it. A row that reappeared in Squid, was imported by another confirmation,
/// lost its owner or was never offered refuses the plan as stale instead of importing it twice; a forged choice or action on a row
/// refuses it too, and so does a choosable kind posted as created that the server's plan does not create (a forged create would
/// otherwise be inserted into an imported owner). Runs at validation and again at confirmation (with conflicts read then).
/// </summary>
public partial class OctopusImportPreviewValidator
{
    private void ValidateChoices(OctopusImportDependencyPlan dependencyPlan, OctopusImportConflictDiscoveryResult conflicts, OctopusImportPreviewPlanDto previewPlan, OctopusImportPreviewPlanDto serverPlan, OctopusImportValidationResultDto result)
    {
        var fresh = ById((serverPlan ?? _previewPlanner.BuildPreviewPlan(dependencyPlan, conflicts, OctopusImportChoiceSelections.From(previewPlan))).Resources);
        var posted = ById(previewPlan.Resources);

        foreach (var sourceId in ChoiceRowIds(posted).Union(ChoiceRowIds(fresh), StringComparer.OrdinalIgnoreCase).Union(CreatedChildIds(posted), StringComparer.OrdinalIgnoreCase))
        {
            var postedRow = posted.GetValueOrDefault(sourceId);

            if (ChoiceChange(postedRow, fresh.GetValueOrDefault(sourceId)) is { } change)
                result.Diagnostics.Add(ChoiceStale(postedRow ?? fresh[sourceId], change));
        }
    }

    private static Dictionary<string, OctopusImportResourceResultDto> ById(IEnumerable<OctopusImportResourceResultDto> resources)
        => resources.Where(r => !string.IsNullOrWhiteSpace(r.SourceId)).GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ChoiceRowIds(IReadOnlyDictionary<string, OctopusImportResourceResultDto> rows)
        => rows.Values.Where(r => r.Choice != null).Select(r => r.SourceId);

    private static IEnumerable<string> CreatedChildIds(IReadOnlyDictionary<string, OctopusImportResourceResultDto> rows)
        => rows.Values.Where(r => r.PreviewAction == OctopusImportPreviewAction.Create && Enum.TryParse<Octopus.OctopusResourceKind>(r.SourceType, out var kind) && OctopusImportPriorMappingPolicy.IsChoosable(kind)).Select(r => r.SourceId);

    // Why the row the user decided about is no longer what the plan showed, or null when it still is.
    private static string ChoiceChange(OctopusImportResourceResultDto posted, OctopusImportResourceResultDto fresh)
    {
        if (fresh == null) return "it is not in the import source any more";
        if (posted?.Choice == null && fresh.Choice == null) return posted?.PreviewAction == fresh.PreviewAction ? null : "it is not created by this import";
        if (fresh.Choice == null) return WhyNotOffered(fresh);
        if (posted?.Choice == null) return "it is offered to decide on, but the plan does not show it";
        if (!IsSameChoice(posted.Choice, fresh.Choice)) return "what it is offered for changed";

        return posted.PreviewAction == fresh.PreviewAction && posted.DestinationId == fresh.DestinationId ? null : "the plan does not match what was selected";
    }

    private static string WhyNotOffered(OctopusImportResourceResultDto fresh)
        => fresh.PreviewAction switch
        {
            OctopusImportPreviewAction.AlreadyImported when fresh.DestinationId != null => "it was imported by another confirmation",
            OctopusImportPreviewAction.AlreadyImported or OctopusImportPreviewAction.ReuseExisting or OctopusImportPreviewAction.RenameRequired => "it is in Squid again",
            _ => "what it belongs to is no longer imported, or it is not offered to decide on"
        };

    private static bool IsSameChoice(OctopusImportChoiceDto posted, OctopusImportChoiceDto fresh)
        => posted.Reason == fresh.Reason && string.Equals(posted.FollowsSourceId, fresh.FollowsSourceId, StringComparison.OrdinalIgnoreCase);

    private static OctopusImportDiagnosticDto ChoiceStale(OctopusImportResourceResultDto row, string change)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Blocker,
            Code = OctopusImportPreviewDiagnosticCodes.StalePreviewPlan,
            Message = $"Octopus {row.SourceType} '{row.SourceName}' changed since the preview: {change}. Re-run the preview.",
            SourceId = row.SourceId,
            ResourceType = row.SourceType,
            ResourceName = row.SourceName
        });
}
