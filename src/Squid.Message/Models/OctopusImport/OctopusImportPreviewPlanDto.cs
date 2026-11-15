using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportPreviewPlanDto
{
    public DateTimeOffset GeneratedAt { get; set; }

    public List<OctopusImportResourceResultDto> Resources { get; set; } = [];

    public List<OctopusImportRequiredInputDto> RequiredInputs { get; set; } = [];

    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];

    /// <summary>Set when this import would fill missing secrets from an export file whose installation the user must confirm; null otherwise.</summary>
    public OctopusImportFillPairingDto FillPairing { get; set; }

    /// <summary>Totals, per-kind and per-project counts, skip reasons and the "up to date" flag; null for plans produced before it existed.</summary>
    public OctopusImportPreviewSummaryDto Summary { get; set; }

    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker)
        || Resources.Any(r => r.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker));
}
