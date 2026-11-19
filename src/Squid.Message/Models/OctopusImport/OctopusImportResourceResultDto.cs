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

    /// <summary>The Octopus project the resource belongs to (the project itself for a project), so a client can group history per project. Null for shared resources.</summary>
    public string OwnerProjectSourceId { get; set; }

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

    /// <summary>
    /// For a resource that speaks for a secret an earlier import left missing: the Squid project / library variable set / feed it is
    /// (or would be) filled in. Null for every other resource.
    /// </summary>
    public OctopusImportFillDestinationDto FillDestination { get; set; }

    /// <summary>
    /// For an Update of imported history: what the source map held for it when the preview was generated. Confirmation refuses the update
    /// once a later import has moved that record on, so an older copy of the Octopus content never replaces newer imported content. Null
    /// for every other action and for plans produced before it existed.
    /// </summary>
    public string PriorImportFingerprint { get; set; }

    /// <summary>For an Update or a Fill: the fields written into the row an earlier import created, old → new (a secret is named, never shown). Empty otherwise.</summary>
    public List<OctopusImportFieldChangeDto> Changes { get; set; } = [];

    /// <summary>
    /// Set when the user decides whether this row is imported: it was deleted in Squid after an earlier import, or added in Octopus after
    /// what it belongs to was imported. Null for every other row and for plans produced before it existed.
    /// </summary>
    public OctopusImportChoiceDto Choice { get; set; }

    /// <summary>For a resource that is not imported (Skip, Unsupported): why. Null otherwise.</summary>
    public OctopusImportSkipReasonDto SkipReason { get; set; }

    public List<OctopusImportRequiredInputDto> RequiredInputs { get; set; } = [];

    public List<OctopusImportDiagnosticDto> Diagnostics { get; set; } = [];
}
