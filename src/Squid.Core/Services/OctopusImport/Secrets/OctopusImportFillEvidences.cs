using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of truth for "does this source come from the Octopus installation this space was imported from?" — asked before a
/// source may link to rows an earlier import created (the whole import) and before it may fill secrets. Ids like <c>Projects-1</c> or
/// names collide across servers (a cloned staging/production pair), so they never count. Proof, in order: the same installation id
/// (two different ones refuse outright); the export fingerprint (id suffix) the space's FIRST export-file import recorded (any other
/// fingerprint is another export source: its history is skipped and it fills nothing); otherwise process step / action / lifecycle
/// phase GUIDs (random per installation, identical in the API and the export) that an earlier import of this space created — and then
/// only with the user's explicit confirmation of the pairing.
/// </summary>
public static class OctopusImportFillEvidences
{
    private static readonly HashSet<OctopusResourceKind> StructureKinds = [OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction, OctopusResourceKind.LifecyclePhase];

    public const string InstallationBasis = "installation";

    public const string ExportFingerprintBasis = "export-fingerprint";

    public const string ConfirmedPairingBasis = "confirmed-pairing";

    /// <summary>Why an evidence is <c>Unproven</c> although structure ids may match: the file's fingerprint differs from the recorded one.</summary>
    public const string ExportFingerprintMismatchBasis = "export-fingerprint-mismatch";

    /// <summary>Why an evidence is <c>Unproven</c> whatever else matches: the source's installation id differs from the recorded one.</summary>
    public const string InstallationMismatchBasis = "installation-mismatch";

    public static OctopusImportFillEvidence Evaluate(string sourceInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior, bool pairingConfirmed)
    {
        prior ??= OctopusImportPriorMappings.Empty;

        var matches = MatchingStructureIds(nodes, prior);

        if (SameId(sourceInstallationId, prior.InstallationId)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, InstallationBasis);

        if (IsAnotherInstallation(sourceInstallationId, prior)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Unproven, matches, InstallationMismatchBasis);

        var recorded = RecordedExportSourceId(prior);

        if (SameId(fileExportSourceId, recorded)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, ExportFingerprintBasis);

        // Both carry fingerprints and the file's is not the pinned one: another export source, whatever ids or names match
        // (a cloned server shares them).
        if (HasId(fileExportSourceId) && HasId(recorded)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Unproven, matches, ExportFingerprintMismatchBasis);

        if (matches == 0) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Unproven, 0, null);

        return pairingConfirmed ? new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, ConfirmedPairingBasis) : new OctopusImportFillEvidence(OctopusImportFillEvidenceState.PairingRequired, matches, null);
    }

    /// <summary>
    /// The export file's fingerprint differs from the one pinned for the space: it may come from another Octopus server that shares ids like
    /// <c>Releases-1</c>, so nothing it would write over earlier imported rows (history, secrets) is trusted.
    /// </summary>
    public static bool IsAnotherExportSource(OctopusImportFillEvidence evidence) => evidence?.Basis == ExportFingerprintMismatchBasis;

    /// <summary>Both the source and the space carry an installation id and they differ: another Octopus server, whatever else matches.</summary>
    public static bool IsAnotherInstallation(string sourceInstallationId, OctopusImportPriorMappings prior)
        => HasId(sourceInstallationId) && HasId(prior?.InstallationId) && !SameId(sourceInstallationId, prior.InstallationId);

    /// <summary>
    /// The source would link to rows an earlier import of a RECORDED origin created, so it must prove it reads the same Octopus. A first
    /// import, a source with no id in common, and a space with no recorded origin (every import records one) need no proof.
    /// </summary>
    public static bool NeedsOriginProof(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => HasRecordedOrigin(prior) && HitsEarlierImport(nodes, prior);

    /// <summary>
    /// Some resource of the source has a live source-map row of its kind: the import would treat it as already imported and link to it.
    /// Octopus's built-in teams carry the same id on every installation by design, so they link nothing server-specific.
    /// </summary>
    public static bool HitsEarlierImport(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => (nodes ?? []).Any(n => !IsSameOnEveryInstallation(n) && (prior ?? OctopusImportPriorMappings.Empty).TryGet(n.Kind.ToString(), n.SourceId, out _));

    /// <summary>
    /// An origin that is not proven refuses linking to earlier rows — except another export fingerprint of a file, which degrades
    /// instead (its history is skipped with a reason, configuration links are kept, nothing is overwritten).
    /// </summary>
    public static bool RefusesLinking(OctopusImportFillEvidence evidence)
        => evidence?.State == OctopusImportFillEvidenceState.Unproven && !IsAnotherExportSource(evidence);

    /// <summary>The fingerprint pinned for the space: the one its first export-file import recorded.</summary>
    public static string RecordedExportSourceId(OctopusImportPriorMappings prior) => prior?.ExportSourceIds.FirstOrDefault();

    /// <summary>Distinct GUID ids of the source's current steps, actions and lifecycle phases that an earlier import of this space created.</summary>
    public static int MatchingStructureIds(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => (nodes ?? []).Where(n => StructureKinds.Contains(n.Kind) && !n.IsHistorical && Guid.TryParse(n.SourceId, out _))
            .Where(n => prior.TryGet(n.Kind.ToString(), n.SourceId, out var mapping) && mapping.OutcomeState == OctopusImportSourceMapQueries.CreatedOutcome)
            .Select(n => OctopusImportPriorMappings.Normalize(n.SourceId))
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>The space records where its imports came from: an installation id or a pinned export fingerprint.</summary>
    public static bool HasRecordedOrigin(OctopusImportPriorMappings prior) => HasId(prior?.InstallationId) || prior?.ExportSourceIds.Count > 0;

    private static bool IsSameOnEveryInstallation(OctopusResourceNode node) => node.Kind == OctopusResourceKind.Team && OctopusBuiltInTeams.IsBuiltIn(node.SourceId);

    private static bool SameId(string incoming, string recorded)
        => HasId(incoming) && HasId(recorded) && OctopusImportPriorMappings.Normalize(incoming) == OctopusImportPriorMappings.Normalize(recorded);

    private static bool HasId(string id) => !string.IsNullOrWhiteSpace(id);
}
