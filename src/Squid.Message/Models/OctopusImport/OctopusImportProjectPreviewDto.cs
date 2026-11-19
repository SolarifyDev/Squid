namespace Squid.Message.Models.OctopusImport;

/// <summary>One Octopus project in the preview: where it lands in Squid and what this import changes for it (the project and everything it owns).</summary>
public class OctopusImportProjectPreviewDto : OctopusImportPreviewGroupDto
{
    public string SourceId { get; set; }

    public string SourceName { get; set; }

    public OctopusImportProjectDestinationDto Destination { get; set; } = new();
}
