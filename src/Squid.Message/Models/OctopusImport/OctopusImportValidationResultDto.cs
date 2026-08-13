using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportValidationResultDto
{
    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];

    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
