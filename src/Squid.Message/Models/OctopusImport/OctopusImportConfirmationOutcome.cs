namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// What a confirmation request ended with: the session as it stands now, and the blocker that refused it
/// before anything was imported (null when the confirmation ran, was queued, or had already run).
/// </summary>
public sealed record OctopusImportConfirmationOutcome(OctopusImportSessionDto Session, OctopusImportDiagnosticDto Rejection = null)
{
    public bool IsRejected => Rejection != null;
}
