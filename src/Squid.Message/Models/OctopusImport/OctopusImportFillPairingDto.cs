namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The first pairing of an import source (an export file or an Octopus server) with the Octopus a space was imported from — present
/// when the source would link to what earlier imports created, or fill secrets they left missing, and only matching process ids tie
/// it to that Octopus. The server sets every field but <see cref="Confirmed"/>; the client sets <see cref="Confirmed"/> when the user
/// confirms that both come from the same Octopus server, and posts the plan back to validate.
/// </summary>
public class OctopusImportFillPairingDto
{
    /// <summary>The import needs the user's confirmation (the source carries no proof of its installation, only matching ids).</summary>
    public bool Required { get; set; }

    public bool Confirmed { get; set; }

    /// <summary>The Octopus installation this space was imported from.</summary>
    public string InstallationId { get; set; }

    /// <summary>The Octopus server (host[:port]) the space's API import read; null when no import session records it any more.</summary>
    public string ServerHost { get; set; }

    /// <summary>The Octopus space that import read (e.g. <c>Spaces-1</c>); null when unknown.</summary>
    public string OctopusSpaceId { get; set; }

    /// <summary>The Octopus server version that import read; null when unknown.</summary>
    public string OctopusVersion { get; set; }

    /// <summary>The Octopus installation the incoming source reads (an Octopus server); null for an export file.</summary>
    public string SourceInstallationId { get; set; }

    /// <summary>The Octopus server (host[:port]) the incoming source reads; null for an export file or when unknown.</summary>
    public string SourceServerHost { get; set; }

    /// <summary>The Octopus space the incoming source reads; null for an export file or when unknown.</summary>
    public string SourceOctopusSpaceId { get; set; }

    /// <summary>The export fingerprint (id suffix) of this file; null when its ids carry none.</summary>
    public string FileExportSourceId { get; set; }

    /// <summary>The export fingerprint recorded for this space by the first export-file import; null when none.</summary>
    public string RecordedExportSourceId { get; set; }

    /// <summary>How many process step, action and lifecycle phase ids of this file were imported into this space before.</summary>
    public int MatchingStructureIds { get; set; }
}
