using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of the import-level "export password unavailable" diagnostic, so the preview planner
/// and the (preview- and confirmation-time) validator report an unreadable export password identically.
/// A Warning, not a Blocker: the sensitive values it affects already surface as required-input blockers;
/// this explains why, and an export whose only secrets are not imported is not blocked by it.
/// </summary>
public static class OctopusImportExportSecretDiagnostics
{
    public static IEnumerable<OctopusImportDiagnosticDto> Describe(OctopusExportSecrets secrets)
    {
        if (secrets?.IsUnreadable != true) yield break;

        yield return new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Warning,
            Code = OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable,
            Message = OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailableMessage
        };
    }
}
