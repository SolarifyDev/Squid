using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of the import-level "export password unavailable" diagnostic, so the preview planner
/// and the (preview- and confirmation-time) validator report an unreadable or not-retained export password
/// identically. One code, two messages: only an unreadable envelope can be recovered by restoring a key.
/// A Warning, not a Blocker: the sensitive values it affects already surface as required-input blockers;
/// this explains why, and an export whose only secrets are not imported is not blocked by it.
/// </summary>
public static class OctopusImportExportSecretDiagnostics
{
    public static IEnumerable<OctopusImportDiagnosticDto> Describe(OctopusExportSecrets secrets)
    {
        var message = UnavailabilityMessage(secrets);

        if (message == null) yield break;

        yield return new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Warning,
            Code = OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable,
            Message = message
        };
    }

    private static string UnavailabilityMessage(OctopusExportSecrets secrets)
    {
        if (secrets?.IsUnreadable == true) return OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailableMessage;

        return secrets?.IsNotRetained == true ? OctopusImportPreviewDiagnosticCodes.ExportPasswordNotRetainedMessage : null;
    }
}
