using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The last confirmation attempt of a session that is Validated again: the attempt imported nothing (its transaction rolled back, or it
/// never committed), so the same validated plan can be imported again without reading the source again.
/// </summary>
public class OctopusImportConfirmationFailureDto
{
    public Guid AttemptId { get; set; }

    public DateTimeOffset FailedAt { get; set; }

    public OctopusImportConfirmationFailureReason Reason { get; set; }

    public bool Transient { get; set; }

    public int Attempts { get; set; }

    /// <summary>Redacted, blockers first, at most 20.</summary>
    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];
}
