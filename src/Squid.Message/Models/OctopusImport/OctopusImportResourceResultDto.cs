using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportResourceResultDto
{
    public string SourceId { get; set; }

    public string SourceType { get; set; }

    public string SourceName { get; set; }

    public OctopusImportPreviewAction PreviewAction { get; set; }

    public OctopusImportResourceOutcomeState OutcomeState { get; set; }

    public int? DestinationId { get; set; }

    /// <summary>
    /// Member count of a reused destination team when the preview was generated. Reusing a team grants
    /// manual-intervention approval to its members, so confirmation treats any change of this count as a
    /// stale preview. Null for every other resource kind and for plans produced before it existed.
    /// </summary>
    public int? DestinationMemberCount { get; set; }

    /// <summary>
    /// Fingerprint of the reused destination team's member set when the preview was generated, so confirmation
    /// also catches a member being swapped for another (same count, different people). Null for every other
    /// resource kind, for the built-in Everyone team (it means every user) and for plans produced before it existed.
    /// </summary>
    public string DestinationMemberFingerprint { get; set; }

    public List<OctopusImportRequiredInputDto> RequiredInputs { get; set; } = [];

    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];
}
