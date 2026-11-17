using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of truth for "does this source come from the Octopus installation this space was imported from?" — asked before a
/// source may link to rows an earlier import created (the whole import) and before it may fill secrets. Ids like <c>Projects-1</c> or
/// names collide across servers (a cloned staging/production pair), so they never count. Proof, in order: the same installation id
/// (two different ones refuse outright); an export fingerprint (id suffix) recorded for the space — the one its first export-file import
/// pinned, or one a confirmed pairing later added as another export of the same Octopus; otherwise process step / action / lifecycle
/// phase GUIDs (random per installation, identical in the API and the export) that an earlier import of this space's origin created —
/// and then only with the user's explicit confirmation of the pairing. A file whose fingerprint the space does not know is no
/// exception: it is checked by those GUIDs like any other source. Every row remembers the origin of the import that created it, so an
/// import links freely only to its own rows; a row of the recorded origin (or of unknown origin) needs that proof, and a row a third
/// origin created is never linked — however the source is proven.
/// </summary>
public static class OctopusImportFillEvidences
{
    private static readonly HashSet<OctopusResourceKind> StructureKinds = [OctopusResourceKind.DeploymentStep, OctopusResourceKind.DeploymentAction, OctopusResourceKind.LifecyclePhase];

    public const string InstallationBasis = "installation";

    public const string ExportFingerprintBasis = "export-fingerprint";

    public const string ConfirmedPairingBasis = "confirmed-pairing";

    /// <summary>
    /// Why a file needs the pairing (<c>PairingRequired</c>) or is refused (<c>Unproven</c>, no process id in common): its fingerprint is
    /// not one the space recorded.
    /// </summary>
    public const string ExportFingerprintMismatchBasis = "export-fingerprint-mismatch";

    /// <summary>Why an evidence is <c>Unproven</c> whatever else matches: the source's installation id differs from the recorded one.</summary>
    public const string InstallationMismatchBasis = "installation-mismatch";

    /// <summary>Why an evidence is <c>Unproven</c> whatever else matches: the source would link to rows a third origin created.</summary>
    public const string OtherOriginRowsBasis = "other-origin-rows";

    public static OctopusImportFillEvidence Evaluate(string sourceInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior, bool pairingConfirmed)
    {
        prior ??= OctopusImportPriorMappings.Empty;

        var evidence = EvaluateSource(sourceInstallationId, fileExportSourceId, nodes, prior, pairingConfirmed);

        if (evidence.State == OctopusImportFillEvidenceState.Unproven || !HitsRowsOfAnotherOrigin(sourceInstallationId, fileExportSourceId, nodes, prior)) return evidence;

        return evidence with { State = OctopusImportFillEvidenceState.Unproven, Basis = OtherOriginRowsBasis };
    }

    // Is the source the space's recorded origin? By id, by fingerprint, or by a confirmed pairing backed by the origin's structure ids.
    private static OctopusImportFillEvidence EvaluateSource(string sourceInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior, bool pairingConfirmed)
    {
        var matches = MatchingStructureIds(nodes, prior);

        if (SameId(sourceInstallationId, prior.InstallationId)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, InstallationBasis);

        if (IsAnotherInstallation(sourceInstallationId, prior)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Unproven, matches, InstallationMismatchBasis);

        if (IsKnownExportSource(fileExportSourceId, prior)) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, ExportFingerprintBasis);

        // A fingerprint the space does not know may be another export of its Octopus or another server sharing its ids: only the process
        // ids decide, and only with the user's confirmation.
        var basis = IsUnknownExportSource(fileExportSourceId, prior) ? ExportFingerprintMismatchBasis : null;

        if (matches == 0) return new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Unproven, 0, basis);

        return pairingConfirmed ? new OctopusImportFillEvidence(OctopusImportFillEvidenceState.Proven, matches, ConfirmedPairingBasis) : new OctopusImportFillEvidence(OctopusImportFillEvidenceState.PairingRequired, matches, basis);
    }

    /// <summary>The file carries a fingerprint the space recorded for its origin (the pinned one or one a confirmed pairing added).</summary>
    public static bool IsKnownExportSource(string fileExportSourceId, OctopusImportPriorMappings prior)
        => (prior?.ExportSourceIds ?? []).Any(recorded => SameId(fileExportSourceId, recorded));

    // Both carry fingerprints and the file's is none of the recorded ones.
    private static bool IsUnknownExportSource(string fileExportSourceId, OctopusImportPriorMappings prior)
        => HasId(fileExportSourceId) && prior.ExportSourceIds.Count > 0 && !IsKnownExportSource(fileExportSourceId, prior);

    /// <summary>Both the source and the space carry an installation id and they differ: another Octopus server, whatever else matches.</summary>
    public static bool IsAnotherInstallation(string sourceInstallationId, OctopusImportPriorMappings prior)
        => HasId(sourceInstallationId) && HasId(prior?.InstallationId) && !SameId(sourceInstallationId, prior.InstallationId);

    /// <summary>
    /// The source would link to a row an import of another (or an unknown) origin created, so it must prove it reads the same Octopus.
    /// A first import, a source with no id in common, and a source that only meets rows it created itself need no proof.
    /// </summary>
    public static bool NeedsOriginProof(string sourceInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => HitRows(nodes, prior).Any(row => !IsCreatedBy(row, sourceInstallationId, fileExportSourceId));

    /// <summary>
    /// The space has rows whose creating origin is unknown and no recorded origin to attribute them to: a source with no id in common
    /// cannot become the origin there, or it would later be proven by id for rows it never created.
    /// </summary>
    public static bool HasRowsOfUnknownOrigin(OctopusImportPriorMappings prior) => (prior?.Mappings.Values ?? []).Any(IsOfUnknownOrigin);

    /// <summary>An origin that is not proven refuses linking to earlier rows, whatever its basis.</summary>
    public static bool RefusesLinking(OctopusImportFillEvidence evidence) => evidence?.State == OctopusImportFillEvidenceState.Unproven;

    /// <summary>The fingerprint pinned for the space: the one its first export-file import recorded (later ones are listed after it).</summary>
    public static string RecordedExportSourceId(OctopusImportPriorMappings prior) => prior?.ExportSourceIds.FirstOrDefault();

    /// <summary>
    /// Distinct GUID ids of the source's current steps, actions and lifecycle phases that an earlier import of the space's recorded
    /// origin created. Rows another origin created (the source's own included) prove nothing about the recorded one.
    /// </summary>
    public static int MatchingStructureIds(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => (nodes ?? []).Where(n => StructureKinds.Contains(n.Kind) && !n.IsHistorical && IsGuid(n.SourceId))
            .Where(n => prior.TryGet(n.Kind.ToString(), n.SourceId, out var mapping) && mapping.OutcomeState == OctopusImportSourceMapQueries.CreatedOutcome && IsOfRecordedOrigin(mapping, prior))
            .Select(n => OctopusImportPriorMappings.Normalize(OctopusImportChildSourceIds.OctopusId(n.SourceId)))
            .Distinct(StringComparer.Ordinal)
            .Count();

    // A child's source id is qualified by its owner; the random part is the Octopus id after the last '/'.
    private static bool IsGuid(string sourceId) => Guid.TryParse(OctopusImportChildSourceIds.OctopusId(sourceId), out _);

    /// <summary>The space records where its imports came from: an installation id or a pinned export fingerprint.</summary>
    public static bool HasRecordedOrigin(OctopusImportPriorMappings prior) => HasId(prior?.InstallationId) || prior?.ExportSourceIds.Count > 0;

    // The live rows the source would link to. Octopus's built-in teams carry the same id on every installation by design, so they link
    // nothing server-specific.
    private static IEnumerable<OctopusImportPriorMapping> HitRows(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
    {
        prior ??= OctopusImportPriorMappings.Empty;

        foreach (var node in (nodes ?? []).Where(n => !IsSameOnEveryInstallation(n)))
            if (prior.TryGet(node.Kind.ToString(), node.SourceId, out var row)) yield return row;
    }

    /// <summary>
    /// The origins — neither the source nor the space's recorded one — that created rows the source would link to: each once, named by
    /// its installation when it has one, else by its export fingerprint, ordered by that id.
    /// </summary>
    public static IReadOnlyList<OctopusImportRowCreator> OtherOriginCreators(string sourceInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => RowsOfAnotherOrigin(sourceInstallationId, fileExportSourceId, nodes, prior)
            .Select(row => HasId(row.CreatedInstallationId) ? new OctopusImportRowCreator(row.CreatedInstallationId.Trim(), null) : new OctopusImportRowCreator(null, row.CreatedExportSourceId.Trim()))
            .GroupBy(CreatorKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

    private static bool HitsRowsOfAnotherOrigin(string sourceInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => RowsOfAnotherOrigin(sourceInstallationId, fileExportSourceId, nodes, prior).Any();

    private static IEnumerable<OctopusImportPriorMapping> RowsOfAnotherOrigin(string sourceInstallationId, string fileExportSourceId, IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => HitRows(nodes, prior).Where(row => !IsCreatedBy(row, sourceInstallationId, fileExportSourceId) && !IsOfRecordedOrigin(row, prior ?? OctopusImportPriorMappings.Empty));

    private static string CreatorKey(OctopusImportRowCreator creator) => OctopusImportPriorMappings.Normalize(creator.InstallationId ?? creator.ExportSourceId);

    private static bool IsCreatedBy(OctopusImportPriorMapping row, string installationId, string exportSourceId)
        => SameId(installationId, row.CreatedInstallationId) || SameId(exportSourceId, row.CreatedExportSourceId);

    // A row of unknown origin predates per-row origins: it is attributed to the space's recorded origin. A row any recorded fingerprint
    // created is the origin's too.
    private static bool IsOfRecordedOrigin(OctopusImportPriorMapping row, OctopusImportPriorMappings prior)
        => IsOfUnknownOrigin(row) || SameId(prior.InstallationId, row.CreatedInstallationId) || IsKnownExportSource(row.CreatedExportSourceId, prior);

    private static bool IsOfUnknownOrigin(OctopusImportPriorMapping row) => !HasId(row.CreatedInstallationId) && !HasId(row.CreatedExportSourceId);

    private static bool IsSameOnEveryInstallation(OctopusResourceNode node) => node.Kind == OctopusResourceKind.Team && OctopusBuiltInTeams.IsBuiltIn(node.SourceId);

    private static bool SameId(string incoming, string recorded)
        => HasId(incoming) && HasId(recorded) && OctopusImportPriorMappings.Normalize(incoming) == OctopusImportPriorMappings.Normalize(recorded);

    private static bool HasId(string id) => !string.IsNullOrWhiteSpace(id);
}
