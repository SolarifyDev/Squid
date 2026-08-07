using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportDiagnosticDto
{
    public OctopusImportCompatibilitySeverity Severity { get; set; }

    public string Code { get; set; }

    public string Message { get; set; }

    public string ResourceType { get; set; }

    public string SourceId { get; set; }

    public string ResourceName { get; set; }
}
