namespace Squid.Message.Models.OctopusImport;

public class OctopusImportSessionResultDto
{
    public bool Succeeded { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public List<OctopusImportResourceResultDto> Resources { get; set; } = [];

    public List<OctopusImportIdMappingDto> IdMappings { get; set; } = [];

    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];
}
