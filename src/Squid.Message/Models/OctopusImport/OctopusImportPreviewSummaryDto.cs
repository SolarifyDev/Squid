namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The preview at a glance, the same for an export file and the Octopus API: totals and per-kind counts for the whole plan, per Octopus
/// project (with the Squid project the changes land on) and for the space-level resources no project owns, the reasons resources are
/// not imported, and whether everything is already up to date.
/// </summary>
public class OctopusImportPreviewSummaryDto
{
    /// <summary>Nothing to create, update, fill or link and nothing blocked: importing again changes nothing in Squid.</summary>
    public bool IsUpToDate { get; set; }

    public OctopusImportChangeCountsDto Counts { get; set; } = new();

    public List<OctopusImportKindChangeCountsDto> Kinds { get; set; } = [];

    public List<OctopusImportProjectPreviewDto> Projects { get; set; } = [];

    /// <summary>Resources no Octopus project owns (environments, feeds, library variable sets, teams, …).</summary>
    public OctopusImportPreviewGroupDto Space { get; set; } = new();

    public List<OctopusImportSkipReasonCountDto> SkipReasons { get; set; } = [];
}
