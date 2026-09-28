namespace Squid.Message.Models.OctopusImport;

public class OctopusImportSourceFileSummaryDto
{
    public string Path { get; set; }

    public string DocumentType { get; set; }

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; }
}
