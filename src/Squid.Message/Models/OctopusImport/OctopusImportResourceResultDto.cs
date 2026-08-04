using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportResourceResultDto
{
    public string SourceId { get; set; }

    public string SourceType { get; set; }

    public string SourceName { get; set; }

    public OctopusImportPreviewAction PreviewAction { get; set; }

    public OctopusImportResourceOutcomeState OutcomeState { get; set; }

    public int? DestinationId { get; set; }

    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];
}
