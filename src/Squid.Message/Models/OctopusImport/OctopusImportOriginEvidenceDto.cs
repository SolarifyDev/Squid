using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The origin check of a whole import, as the preview shows it: present when the import would link to rows an import of another origin
/// created in the space (or its installation differs from the recorded one). <see cref="State"/> is the verdict — refused, pairing
/// required (see <see cref="OctopusImportPreviewPlanDto.FillPairing"/>) or proven — and <see cref="Basis"/> what decided it. Origins are
/// named by installation id, server, space, version and export fingerprint only, never a secret.
/// </summary>
public class OctopusImportOriginEvidenceDto
{
    public OctopusImportFillEvidenceState State { get; set; }

    /// <summary>
    /// <c>installation</c> or <c>export-fingerprint</c> (proven); <c>installation-mismatch</c> or <c>other-origin-rows</c> (refused);
    /// null when only matching process ids could tie the source to the space's Octopus (pairing required, or refused with none).
    /// </summary>
    public string Basis { get; set; }

    /// <summary>How many process step, action and lifecycle phase ids of this source an import of the space's recorded origin created.</summary>
    public int MatchingStructureIds { get; set; }

    /// <summary>The incoming source, e.g. "Octopus installation … (server …, space …)" or "an export file with fingerprint …".</summary>
    public string Source { get; set; }

    /// <summary>What the space was imported from: its recorded installation and / or pinned export fingerprint.</summary>
    public string Recorded { get; set; }

    /// <summary>
    /// For <c>other-origin-rows</c>: the origins that created the resources this import would link to (at most three, worded like
    /// <see cref="Source"/>); null otherwise.
    /// </summary>
    public List<string> Creators { get; set; }

    /// <summary>How many more such origins there are beyond <see cref="Creators"/>.</summary>
    public int MoreCreators { get; set; }
}
