using System.Linq;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// The whole-import origin gate: ids like <c>Projects-1</c> repeat across Octopus servers, so a source that would link to rows an
/// earlier import created must come from the Octopus the space was imported from. Every branch is a row, so a changed rule turns red.
/// </summary>
public class OctopusImportOriginEvidenceTests
{
    private const string ActionGuid = "7a1d0c3e-0000-4000-8000-000000000001";
    private const string FingerprintA = "0123456789ABCDEF0123456789ABCDEF";
    private const string FingerprintB = "FEDCBA9876543210FEDCBA9876543210";

    // ── Does the source link to earlier imported rows? ─────────────────────

    [Theory]
    [InlineData("Project", "Projects-1", "Project", "Projects-1", true)]
    [InlineData("Project", "projects-1", "Project", "Projects-1", true)]                                    // ids are compared canonically
    [InlineData("Release", "Releases-1-" + FingerprintA, "Release", "Releases-1", true)]                   // an export suffix is stripped
    [InlineData("Project", "Projects-2", "Project", "Projects-1", false)]
    [InlineData("Environment", "Projects-1", "Project", "Projects-1", false)]                             // same id, other kind: no row
    [InlineData("Team", "teams-everyone", "Team", "teams-everyone", false)]                               // the same on every installation
    [InlineData("Team", "teams-spacemanagers-Spaces-1", "Team", "teams-spacemanagers-Spaces-1", false)]
    [InlineData("Team", "Teams-7", "Team", "Teams-7", true)]
    [InlineData("Feed", "Feeds-1", OctopusImportSourceMapQueries.FeedSecretSourceType, "Feeds-1", false)] // a secret row is not a link
    public void NeedsOriginProof_OnlyALiveRowOfTheSameKindAndId(string nodeKind, string nodeId, string rowType, string rowId, bool expected)
    {
        var prior = Prior(null, [], (rowType, rowId));

        OctopusImportFillEvidences.NeedsOriginProof("inst-x", null, [Node(nodeId, Enum.Parse<OctopusResourceKind>(nodeKind))], prior).ShouldBe(expected);
    }

    // Who created the row the source meets decides whether it needs proof: only its own rows are free to link.
    [Theory]
    [InlineData("inst-a", null, null, null, true, true)]
    [InlineData(null, FingerprintA, null, null, true, true)]
    [InlineData("inst-a", FingerprintA, null, null, true, true)]
    [InlineData("inst-a", null, null, null, false, false)]           // a first import, or one with no id in common
    [InlineData(null, null, null, null, true, true)]                 // no origin recorded, row of unknown origin: proof (a pairing) still needed
    [InlineData(null, FingerprintA, "inst-x", null, true, false)]    // the source's own row (its installation created it)
    [InlineData(null, FingerprintA, "INST-X", null, true, false)]    // ids compared canonically
    [InlineData("inst-a", null, null, FingerprintA, true, false)]    // the source's own row (its export fingerprint created it)
    [InlineData(null, FingerprintA, "inst-y", null, true, true)]     // a row another server created
    public void NeedsOriginProof_WhenTheSourceMeetsARowItDidNotCreate(string recordedInstallation, string recordedFingerprint, string rowInstallation, string rowFingerprint, bool overlapping, bool expected)
    {
        var prior = Prior(recordedInstallation, recordedFingerprint == null ? [] : [recordedFingerprint], ("Project", "Projects-1", rowInstallation, rowFingerprint));

        OctopusImportFillEvidences.NeedsOriginProof("inst-x", rowFingerprint == null ? null : FingerprintA, [Node(overlapping ? "Projects-1" : "Projects-9", OctopusResourceKind.Project)], prior).ShouldBe(expected);
    }

    // Only structure rows of the recorded origin (or of unknown origin, which predates per-row origins) back a pairing: the source's
    // own rows — created by an earlier import of it that the space did not adopt as origin — prove nothing about the recorded origin.
    [Theory]
    [InlineData(null, null, OctopusImportFillEvidenceState.PairingRequired, 1)]       // unknown origin: attributed to the recorded one
    [InlineData(null, FingerprintA, OctopusImportFillEvidenceState.PairingRequired, 1)] // created by the recorded file
    [InlineData("inst-x", null, OctopusImportFillEvidenceState.Unproven, 0)]          // created by the source itself: no evidence
    [InlineData("inst-y", null, OctopusImportFillEvidenceState.Unproven, 0)]          // created by a third server: no evidence
    public void Evaluate_PairingBackedOnlyByStructureRowsOfTheRecordedOrigin(string rowInstallation, string rowFingerprint, OctopusImportFillEvidenceState expectedState, int expectedMatches)
    {
        var prior = Prior(null, [FingerprintA], ("DeploymentAction", ActionGuid, rowInstallation, rowFingerprint));

        var evidence = OctopusImportFillEvidences.Evaluate("inst-x", null, [Node(ActionGuid, OctopusResourceKind.DeploymentAction)], prior, pairingConfirmed: false);

        evidence.State.ShouldBe(expectedState);
        evidence.MatchingStructureIds.ShouldBe(expectedMatches);
    }

    // A source proven by id (or a confirmed pairing) still never links to a row a third origin created: the space's origin vouches only
    // for its own rows. Another export fingerprint keeps degrading (decision B) and a refusal keeps its own basis.
    [Theory]
    [InlineData("inst-a", null, "inst-a", null, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.OtherOriginRowsBasis)]
    [InlineData(null, FingerprintA, null, FingerprintA, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.OtherOriginRowsBasis)]
    [InlineData(null, FingerprintA, "inst-x", null, true, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.OtherOriginRowsBasis)]
    [InlineData(null, FingerprintA, null, FingerprintB, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis)]
    [InlineData("inst-a", null, "inst-b", null, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.InstallationMismatchBasis)]
    public void Evaluate_RowsOfAThirdOrigin_NeverLinked(string recordedInstallation, string recordedFingerprint, string sourceInstallation, string sourceFingerprint, bool confirmed, OctopusImportFillEvidenceState expectedState, string expectedBasis)
    {
        var prior = Prior(recordedInstallation, recordedFingerprint == null ? [] : [recordedFingerprint], ("DeploymentAction", ActionGuid, null, null), ("Project", "Projects-1", "inst-third", null));

        var evidence = OctopusImportFillEvidences.Evaluate(sourceInstallation, sourceFingerprint, [Node(ActionGuid, OctopusResourceKind.DeploymentAction), Node("Projects-1", OctopusResourceKind.Project)], prior, confirmed);

        evidence.State.ShouldBe(expectedState);
        evidence.Basis.ShouldBe(expectedBasis);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("inst-a", null, false)]
    [InlineData(null, FingerprintA, false)]
    public void HasRowsOfUnknownOrigin_OnlyARowWithNoCreatingOrigin(string rowInstallation, string rowFingerprint, bool expected)
    {
        OctopusImportFillEvidences.HasRowsOfUnknownOrigin(Prior(null, [], ("Project", "Projects-1", rowInstallation, rowFingerprint))).ShouldBe(expected);
        OctopusImportFillEvidences.HasRowsOfUnknownOrigin(OctopusImportPriorMappings.Empty).ShouldBeFalse();
    }

    // ── What the evidence means for the whole import ───────────────────────

    [Theory]
    [InlineData(OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.InstallationBasis, false)]
    [InlineData(OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ConfirmedPairingBasis, false)]
    [InlineData(OctopusImportFillEvidenceState.PairingRequired, null, false)]                                       // asks, never refuses
    [InlineData(OctopusImportFillEvidenceState.Unproven, null, true)]                                               // no structure id in common
    [InlineData(OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.InstallationMismatchBasis, true)]
    [InlineData(OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis, false)] // degrades: history skipped
    public void RefusesLinking_UnprovenOriginsExceptAnotherExportFingerprint(OctopusImportFillEvidenceState state, string basis, bool expected)
    {
        OctopusImportFillEvidences.RefusesLinking(new OctopusImportFillEvidence(state, 0, basis)).ShouldBe(expected);
    }

    // A confirmed pairing never overrides two different installation ids: two REST imports of different servers.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherInstallation_UnprovenWhateverStructureMatchesOrTheUserConfirms(bool confirmed)
    {
        var prior = Prior("inst-a", [], ("DeploymentAction", ActionGuid));

        var evidence = OctopusImportFillEvidences.Evaluate("inst-b", null, [Node(ActionGuid, OctopusResourceKind.DeploymentAction)], prior, confirmed);

        evidence.State.ShouldBe(OctopusImportFillEvidenceState.Unproven);
        evidence.Basis.ShouldBe(OctopusImportFillEvidences.InstallationMismatchBasis);
        evidence.MatchingStructureIds.ShouldBe(1);
    }

    [Theory]
    [InlineData("inst-b", "inst-a", true)]
    [InlineData("INST-A", "inst-a", false)]
    [InlineData(null, "inst-a", false)]
    [InlineData("inst-b", null, false)]
    public void IsAnotherInstallation_OnlyTwoKnownDifferentIds(string incoming, string recorded, bool expected)
    {
        OctopusImportFillEvidences.IsAnotherInstallation(incoming, new OctopusImportPriorMappings(null, recorded)).ShouldBe(expected);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static OctopusResourceNode Node(string id, OctopusResourceKind kind)
        => new(id, id, kind, OctopusDocumentKind.Unknown, $"{id}.json", null, null, false, new object());

    private static OctopusImportPriorMappings Prior(string installationId, IReadOnlyList<string> exportSourceIds, params (string Type, string Id)[] rows)
        => Prior(installationId, exportSourceIds, rows.Select(r => (r.Type, r.Id, (string)null, (string)null)).ToArray());

    private static OctopusImportPriorMappings Prior(string installationId, IReadOnlyList<string> exportSourceIds, params (string Type, string Id, string CreatedInstallationId, string CreatedExportSourceId)[] rows)
        => new(rows.ToDictionary(r => OctopusImportPriorMappings.Key(r.Type, r.Id), r => new OctopusImportPriorMapping(900, OctopusImportSourceMapQueries.CreatedOutcome, null, null, r.CreatedInstallationId, r.CreatedExportSourceId)), installationId, exportSourceIds);
}
