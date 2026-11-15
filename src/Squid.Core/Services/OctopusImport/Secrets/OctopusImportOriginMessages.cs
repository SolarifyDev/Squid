using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// The words preview, validation and confirmation use when an import's origin is checked against the Octopus a space was imported from.
/// Origins are named by installation id, server, space, version and export fingerprint only — never a secret.
/// </summary>
public static class OctopusImportOriginMessages
{
    private const string CollidingIds = "Ids like Projects-1 or Releases-1 repeat across Octopus servers";

    /// <summary>The incoming source: the Octopus server it reads, or the export file by its fingerprint.</summary>
    public static string Source(string installationId, string exportSourceId, OctopusImportInstallationOrigin origin)
    {
        if (!string.IsNullOrWhiteSpace(installationId)) return $"Octopus installation {installationId}{OriginText(origin)}";

        return string.IsNullOrWhiteSpace(exportSourceId) ? "a source that records neither an Octopus installation nor an export fingerprint" : $"an export file with fingerprint {exportSourceId}";
    }

    /// <summary>What the space's earlier imports recorded: the installation and / or the pinned export fingerprint.</summary>
    public static string Recorded(OctopusImportPriorMappings prior, OctopusImportInstallationOrigin origin)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(prior?.InstallationId) ? null : $"Octopus installation {prior.InstallationId}{OriginText(origin)}",
            OctopusImportFillEvidences.RecordedExportSourceId(prior) is { } fingerprint ? $"an export file with fingerprint {fingerprint}" : null
        };

        var recorded = parts.Where(p => p != null).ToList();

        return recorded.Count == 0 ? "earlier imports that recorded neither an Octopus installation nor an export fingerprint" : string.Join(" and ", recorded);
    }

    public static string InstallationMismatch(string source, string recorded)
        => $"This source is {source}, but this space was imported from {recorded}. One Squid space holds the history of one Octopus installation, so nothing is linked or written. Import this server into another space.";

    /// <summary>Why linking is refused: rows a third origin created, or nothing proving the source is the space's recorded origin.</summary>
    public static string Refused(OctopusImportFillEvidence evidence, string source, string recorded)
        => evidence?.Basis == OctopusImportFillEvidences.OtherOriginRowsBasis ? OtherOriginRows(source, recorded) : Unproven(evidence?.MatchingStructureIds ?? 0, source, recorded);

    public static string OtherOriginRows(string source, string recorded)
        => $"This import would link to resources that an import of another Octopus source created in this space: neither this source ({source}) nor the one this space was imported from ({recorded}). {CollidingIds}, so nothing is linked or written. Import into another space.";

    public static string Unproven(int matchingStructureIds, string source, string recorded)
        => $"This import would link to resources an earlier import created in this space, but nothing proves it comes from the same Octopus server: this source is {source}, and the space was imported from {recorded}. {CollidingIds}, and {(matchingStructureIds == 0 ? "none" : matchingStructureIds.ToString())} of this source's process step, action and lifecycle phase ids were imported into this space, so nothing is linked or written. Import into another space, or import the Octopus server or export file this space came from.";

    public static string PairingRequired(int matchingStructureIds, string source, string recorded)
        => $"This source is {source}, and this space was imported from {recorded}. {CollidingIds}, so only proof of the same server lets this import link to what is already in Squid: {matchingStructureIds} of its process step, action and lifecycle phase ids were imported into this space. Confirm that both come from the same Octopus server to import; without the confirmation nothing is linked or written. Once confirmed, later imports of either are recognised automatically.";

    public const string PairingUnconfirmed = "This import would link to resources an earlier import created in this space, and its Octopus server is not proven. Confirm the pairing shown in the preview (both come from the same Octopus server), or import into another space.";

    public const string StillUnproven = "Another import of this space recorded a different Octopus origin (or created resources this import would link to) after this plan was validated, or the plan's pairing confirmation is not backed by matching process ids: nothing is linked or written. Re-run the preview.";

    // " (server octopus.example.com, space Spaces-1, Octopus 2026.1)" — whatever the import session recorded.
    public static string OriginText(OctopusImportInstallationOrigin origin)
    {
        var parts = new[] { Part("server", origin?.ServerHost), Part("space", origin?.OctopusSpaceId), Part("Octopus", origin?.OctopusVersion) }.Where(p => p != null).ToList();

        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    private static string Part(string label, string value) => string.IsNullOrWhiteSpace(value) ? null : $"{label} {value}";
}
