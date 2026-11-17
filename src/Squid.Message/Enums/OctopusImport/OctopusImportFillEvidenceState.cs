namespace Squid.Message.Enums.OctopusImport;

/// <summary>
/// Whether an export file is proven to come from the Octopus installation this space was imported from — the condition for
/// filling secrets from it. Never decided by names alone.
/// </summary>
public enum OctopusImportFillEvidenceState
{
    /// <summary>Same installation id, the export fingerprint recorded for the space, or a pairing the user confirmed.</summary>
    Proven = 0,

    /// <summary>Process step / action / lifecycle phase ids of this file match ones imported into the space: the user must confirm the pairing.</summary>
    PairingRequired = 1,

    /// <summary>Nothing ties the file to this space's installation: fills are refused.</summary>
    Unproven = 2
}
