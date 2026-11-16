using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Pure: the preview at a glance, built from the per-resource decisions alone, so an export file and the Octopus API are summarised the
/// same way. Every resource is counted once by its preview action, overall, per kind, under the Octopus project that owns it (or the
/// space), and every project names the Squid project its changes land on.
/// </summary>
public static class OctopusImportPreviewSummaries
{
    public static OctopusImportPreviewSummaryDto Build(IReadOnlyList<OctopusImportResourceResultDto> resources, IReadOnlyList<OctopusImportDestinationResource> destinations, OctopusImportPriorMappings prior, bool hasPlanBlockers)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var whole = Group(resources);

        return new OctopusImportPreviewSummaryDto
        {
            IsUpToDate = whole.IsUpToDate && !hasPlanBlockers,
            Counts = whole.Counts,
            Kinds = whole.Kinds,
            Projects = Projects(resources.Where(r => !IsOutsideSelection(r)).ToList(), destinations ?? [], prior ?? OctopusImportPriorMappings.Empty),
            Space = Group(resources.Where(r => string.IsNullOrWhiteSpace(r.OwnerProjectSourceId)).ToList()),
            SkipReasons = SkipReasons(resources),
            Choices = ChoiceCounts(resources),
            NotChosenProjectCount = resources.Count(r => r.SourceType == nameof(OctopusResourceKind.Project) && HasCode(r, OctopusImportPreviewDiagnosticCodes.ProjectNotChosen))
        };
    }

    /// <summary>Skipped because the session's project selection leaves it out (a project not chosen, or a shared resource no chosen project uses).</summary>
    public static bool IsOutsideSelection(OctopusImportResourceResultDto resource)
        => HasCode(resource, OctopusImportPreviewDiagnosticCodes.ProjectNotChosen) || HasCode(resource, OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects);

    private static bool HasCode(OctopusImportResourceResultDto resource, string code) => resource.Diagnostics.Any(d => d.Code == code);

    /// <summary>Why a resource that is not imported is not imported: the first diagnostic its decision added.</summary>
    public static OctopusImportSkipReasonDto SkipReasonOf(OctopusImportResourceResultDto resource)
    {
        if (resource.PreviewAction is not (OctopusImportPreviewAction.Skip or OctopusImportPreviewAction.Unsupported)) return null;

        var diagnostic = resource.Diagnostics.FirstOrDefault();

        return diagnostic == null ? null : new OctopusImportSkipReasonDto { Code = diagnostic.Code, Message = diagnostic.Message };
    }

    public static bool IsUpToDate(OctopusImportChangeCountsDto counts) => counts.Incremental == 0 && counts.Blocked == 0;

    private static OctopusImportPreviewGroupDto Group(IReadOnlyList<OctopusImportResourceResultDto> resources)
    {
        var counts = Count(resources);

        return new OctopusImportPreviewGroupDto { Counts = counts, Kinds = Kinds(resources), IsUpToDate = IsUpToDate(counts) && !resources.Any(HasBlocker) };
    }

    private static bool HasBlocker(OctopusImportResourceResultDto resource) => resource.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);

    private static List<OctopusImportKindChangeCountsDto> Kinds(IReadOnlyList<OctopusImportResourceResultDto> resources)
        => resources
            .GroupBy(r => r.SourceType, StringComparer.Ordinal)
            .Select(g => new OctopusImportKindChangeCountsDto { Kind = g.Key, Counts = Count(g.ToList()) })
            .ToList();

    private static OctopusImportChangeCountsDto Count(IReadOnlyCollection<OctopusImportResourceResultDto> resources)
    {
        int Of(params OctopusImportPreviewAction[] actions) => resources.Count(r => actions.Contains(r.PreviewAction));

        return new OctopusImportChangeCountsDto
        {
            AlreadyImported = Of(OctopusImportPreviewAction.AlreadyImported),
            Reused = Of(OctopusImportPreviewAction.ReuseExisting),
            Create = Of(OctopusImportPreviewAction.Create),
            Update = Of(OctopusImportPreviewAction.Update),
            Fill = Of(OctopusImportPreviewAction.FillMissingSecret),
            Skip = Of(OctopusImportPreviewAction.Skip),
            Unsupported = Of(OctopusImportPreviewAction.Unsupported),
            Blocked = Of(OctopusImportPreviewAction.Blocked, OctopusImportPreviewAction.RenameRequired),
            Total = resources.Count
        };
    }

    private static List<OctopusImportProjectPreviewDto> Projects(IReadOnlyList<OctopusImportResourceResultDto> resources, IReadOnlyList<OctopusImportDestinationResource> destinations, OctopusImportPriorMappings prior)
        => resources
            .Where(r => !string.IsNullOrWhiteSpace(r.OwnerProjectSourceId))
            .GroupBy(r => r.OwnerProjectSourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => Project(g.Key, g.ToList(), destinations, prior))
            .OrderBy(p => p.SourceName ?? p.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static OctopusImportProjectPreviewDto Project(string projectSourceId, IReadOnlyList<OctopusImportResourceResultDto> owned, IReadOnlyList<OctopusImportDestinationResource> destinations, OctopusImportPriorMappings prior)
    {
        var project = owned.FirstOrDefault(r => r.SourceType == nameof(OctopusResourceKind.Project) && string.Equals(r.SourceId, projectSourceId, StringComparison.OrdinalIgnoreCase));
        var group = Group(owned);

        return new OctopusImportProjectPreviewDto
        {
            SourceId = projectSourceId,
            SourceName = project?.SourceName,
            Destination = Destination(projectSourceId, project, destinations, prior),
            Counts = group.Counts,
            Kinds = group.Kinds,
            IsUpToDate = group.IsUpToDate
        };
    }

    // A project not in this source (history of a project an earlier import created) lands on the project the source map records.
    private static OctopusImportProjectDestinationDto Destination(string projectSourceId, OctopusImportResourceResultDto project, IReadOnlyList<OctopusImportDestinationResource> destinations, OctopusImportPriorMappings prior)
    {
        if (project == null) return prior.TryGet(nameof(OctopusResourceKind.Project), projectSourceId, out var mapping) ? Existing(mapping.DestinationId, destinations) : NotImported();

        return project.PreviewAction switch
        {
            OctopusImportPreviewAction.AlreadyImported or OctopusImportPreviewAction.ReuseExisting when project.DestinationId != null => Existing(project.DestinationId.Value, destinations),
            OctopusImportPreviewAction.Create => new OctopusImportProjectDestinationDto { State = OctopusImportProjectDestinationState.New },
            _ => NotImported()
        };
    }

    private static OctopusImportProjectDestinationDto Existing(int projectId, IReadOnlyList<OctopusImportDestinationResource> destinations)
    {
        var squid = destinations.FirstOrDefault(d => d.Kind == OctopusResourceKind.Project && d.Id == projectId);

        return new OctopusImportProjectDestinationDto { State = OctopusImportProjectDestinationState.Existing, ProjectId = projectId, ProjectName = squid?.Name, ProjectSlug = squid?.Slug };
    }

    private static OctopusImportProjectDestinationDto NotImported() => new() { State = OctopusImportProjectDestinationState.NotImported };

    // Roots only: a follower (an action of a choosable step) is decided with its root and never counted on its own.
    private static List<OctopusImportChoiceCountDto> ChoiceCounts(IReadOnlyList<OctopusImportResourceResultDto> resources)
        => resources
            .Where(r => r.Choice is { FollowsSourceId: null })
            .GroupBy(r => r.Choice.Reason)
            .OrderBy(g => g.Key)
            .Select(g => new OctopusImportChoiceCountDto { Reason = g.Key, Total = g.Count(), Selected = g.Count(r => r.Choice.Selected == true) })
            .ToList();

    private static List<OctopusImportSkipReasonCountDto> SkipReasons(IReadOnlyList<OctopusImportResourceResultDto> resources)
        => resources
            .Where(r => r.SkipReason != null)
            .GroupBy(r => (r.SourceType, r.SkipReason.Code))
            .Select(g => new OctopusImportSkipReasonCountDto { ResourceType = g.Key.SourceType, Code = g.Key.Code, Message = g.First().SkipReason.Message, Count = g.Count() })
            .ToList();
}
