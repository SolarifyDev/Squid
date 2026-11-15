namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The first pairing of an export file with the Octopus installation a space was imported from (only present when this import
/// would fill missing secrets). The server sets every field but <see cref="Confirmed"/>; the client sets <see cref="Confirmed"/>
/// when the user confirms that the file comes from that installation, and posts the plan back to validate.
/// </summary>
public class OctopusImportFillPairingDto
{
    /// <summary>The fills need the user's confirmation (the file carries no proof of its installation, only matching ids).</summary>
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

    /// <summary>The export fingerprint (id suffix) of this file; null when its ids carry none.</summary>
    public string FileExportSourceId { get; set; }

    /// <summary>The export fingerprint recorded for this space by the first export-file import; null when none.</summary>
    public string RecordedExportSourceId { get; set; }

    /// <summary>How many process step, action and lifecycle phase ids of this file were imported into this space before.</summary>
    public int MatchingStructureIds { get; set; }
}
