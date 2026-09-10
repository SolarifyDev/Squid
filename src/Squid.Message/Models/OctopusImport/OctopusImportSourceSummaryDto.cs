namespace Squid.Message.Models.OctopusImport;

public class OctopusImportSourceSummaryDto
{
    public string FileName { get; set; }

    public string ContentType { get; set; }

    public long SizeBytes { get; set; }

    public string DetectedFormat { get; set; }

    public string Sha256 { get; set; }

    public List<OctopusImportSourceFileSummaryDto> Files { get; set; } = [];
}
