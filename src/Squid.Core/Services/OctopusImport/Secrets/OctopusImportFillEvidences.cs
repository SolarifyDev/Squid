using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of truth for "is this export file from the Octopus installation this space was imported from?" — the condition for
/// filling secrets from it. Ids like <c>Projects-1</c> or names collide across servers (a cloned staging/production pair), so they never
/// count. Proof, in order: the same installation id; the export fingerprint (id suffix) the space's FIRST export-file import recorded
/// (any other fingerprint refuses the file outright — a later one may come from a cloned server); otherwise process step / action /
/// lifecycle phase GUIDs (random per installation, identical in the API and the export) that an earlier import of this space created —
/// and then only with the user's explicit confirmation of the pairing.
/// </summary>
public static class OctopusImportFillEvidences
{
    private static readonly HashSet<OctopusResourceKind> StructureKinds = [OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction, OctopusResourceKind.LifecyclePhase];

    public const string InstallationBasis = "installation";

    public const string ExportFingerprintBasis = "export-fingerprint";

    public const string ConfirmedPairingBasis = "confirmed-pairing";

    /// <summary>Why an evidence is <c>Unproven</c> although structure ids may match: the file's fingerprint differs from the recorded one.</summary>
    public const string ExportFingerprintMismatchBasis = "export-fingerprint-mismatch";

    public static OctopusImportFillEvidence Evaluate(string fileInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior, bool pairingConfirmed)
    {
        prior ??= OctopusImportPriorMappings.Empty;

        var matches = MatchingStructureIds(nodes, prior);

        if (SameId(fileInstallationId, prior.InstallationId)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, InstallationBasis);

        var recorded = RecordedExportSourceId(prior);

        if (SameId(fileExportSourceId, recorded)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, ExportFingerprintBasis);

        // Both carry fingerprints and the file's is not the pinned one: another export source, whatever ids or names match
        // (a cloned server shares them).
        if (HasId(fileExportSourceId) && HasId(recorded)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Unproven, matches, ExportFingerprintMismatchBasis);

        if (matches == 0) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Unproven, 0, null);

        return pairingConfirmed ? new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, ConfirmedPairingBasis) : new OctopusImportFillEvidence(OctopusImportFillEvidenceState.PairingRequired, matches, null);
    }

    /// <summary>The fingerprint pinned for the space: the one its first export-file import recorded.</summary>
    public static string RecordedExportSourceId(OctopusImportPriorMappings prior) => prior?.ExportSourceIds.FirstOrDefault();

    /// <summary>Distinct GUID ids of the source's current steps, actions and lifecycle phases that an earlier import of this space created.</summary>
    public static int MatchingStructureIds(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => (nodes ?? []).Where(n => StructureKinds.Contains(n.Kind) && !n.IsHistorical && Guid.TryParse(n.SourceId, out _))
            .Where(n => prior.TryGet(n.Kind.ToString(), n.SourceId, out var mapping) && mapping.OutcomeState == OctopusImportSourceMapQueries.CreatedOutcome)
            .Select(n => OctopusImportPriorMappings.Normalize(n.SourceId))
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static bool SameId(string incoming, string recorded)
        => HasId(incoming) && HasId(recorded) && OctopusImportPriorMappings.Normalize(incoming) == OctopusImportPriorMappings.Normalize(recorded);

    private static bool HasId(string id) => !string.IsNullOrWhiteSpace(id);
}
