using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportExtractionResultDto
{
    public DateTimeOffset ExtractedAt { get; set; }

    public int DocumentCount { get; set; }

    public int ResourceCount { get; set; }

    public List<OctopusImportDocumentCountDto> Counts { get; set; } = [];

    public List<OctopusImportSourceFileSummaryDto> Files { get; set; } = [];

    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];

    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
