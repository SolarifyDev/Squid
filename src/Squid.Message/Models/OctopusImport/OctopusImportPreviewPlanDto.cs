using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportPreviewPlanDto
{
    public List<OctopusImportResourceResultDto> Resources { get; set; } = [];

    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];

    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker)
        || Resources.Any(r => r.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker));
}
