namespace Squid.Message.Models.OctopusImport;

/// <summary>The change counts of a group of resources (one project, or the space-level resources no project owns), overall and per kind.</summary>
public class OctopusImportPreviewGroupDto
{
    public OctopusImportChangeCountsDto Counts { get; set; } = new();

    public List<OctopusImportKindChangeCountsDto> Kinds { get; set; } = [];

    /// <summary>Nothing to create, update, fill or link and nothing blocked.</summary>
    public bool IsUpToDate { get; set; }
}
