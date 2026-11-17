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
    private const string FingerprintC = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

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
    // for its own rows. A file of another export fingerprint is no exception, and a refusal keeps its own basis.
    [Theory]
    [InlineData("inst-a", null, "inst-a", null, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.OtherOriginRowsBasis)]
    [InlineData(null, FingerprintA, null, FingerprintA, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.OtherOriginRowsBasis)]
    [InlineData(null, FingerprintA, "inst-x", null, true, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.OtherOriginRowsBasis)]
    [InlineData(null, FingerprintA, null, FingerprintB, true, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.OtherOriginRowsBasis)]
    [InlineData("inst-a", null, "inst-b", null, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.InstallationMismatchBasis)]
    public void Evaluate_RowsOfAThirdOrigin_NeverLinked(string recordedInstallation, string recordedFingerprint, string sourceInstallation, string sourceFingerprint, bool confirmed, OctopusImportFillEvidenceState expectedState, string expectedBasis)
    {
        var prior = Prior(recordedInstallation, recordedFingerprint == null ? [] : [recordedFingerprint], ("DeploymentAction", ActionGuid, null, null), ("Project", "Projects-1", "inst-third", null));

        var evidence = OctopusImportFillEvidences.Evaluate(sourceInstallation, sourceFingerprint, [Node(ActionGuid, OctopusResourceKind.DeploymentAction), Node("Projects-1", OctopusResourceKind.Project)], prior, confirmed);

        evidence.State.ShouldBe(expectedState);
        evidence.Basis.ShouldBe(expectedBasis);
    }

    // The origins that created the rows a refusal is about: neither the source nor the recorded origin (nor unknown, which is attributed
    // to the recorded one); each named once — by installation when it has one, else by fingerprint — in a fixed order.
    [Fact]
    public void OtherOriginCreators_EachThirdOriginOnceInAFixedOrder()
    {
        var prior = Prior("inst-a", [], ("Project", "Projects-1", "inst-z", null), ("Project", "Projects-2", "INST-Z", FingerprintB), ("Project", "Projects-3", null, FingerprintB), ("Project", "Projects-4", "inst-c", null),
            ("Project", "Projects-5", "inst-a", null), ("Project", "Projects-6", "inst-x", null), ("Project", "Projects-7", null, null), ("Project", "Projects-8", "inst-q", null));
        var nodes = Enumerable.Range(1, 7).Select(i => Node($"Projects-{i}", OctopusResourceKind.Project)).ToList();

        var creators = OctopusImportFillEvidences.OtherOriginCreators("inst-x", null, nodes, prior);

        creators.ShouldBe([new OctopusImportRowCreator(null, FingerprintB), new OctopusImportRowCreator("inst-c", null), new OctopusImportRowCreator("inst-z", null)]);
    }

    [Fact]
    public void OtherOriginCreators_NoneWhenTheSourceMeetsOnlyItsOwnOrTheRecordedOriginsRows()
    {
        var prior = Prior("inst-a", [], ("Project", "Projects-1", "inst-a", null), ("Project", "Projects-2", "inst-x", null), ("Project", "Projects-3", null, null));

        OctopusImportFillEvidences.OtherOriginCreators("inst-x", null, [Node("Projects-1", OctopusResourceKind.Project), Node("Projects-2", OctopusResourceKind.Project), Node("Projects-3", OctopusResourceKind.Project)], prior).ShouldBeEmpty();
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
    [InlineData(OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis, true)]  // another fingerprint, no id in common
    [InlineData(OctopusImportFillEvidenceState.PairingRequired, OctopusImportFillEvidences.ExportFingerprintMismatchBasis, false)]
    public void RefusesLinking_EveryUnprovenOrigin(OctopusImportFillEvidenceState state, string basis, bool expected)
    {
        OctopusImportFillEvidences.RefusesLinking(new OctopusImportFillEvidence(state, 0, basis)).ShouldBe(expected);
    }

    // An export file whose fingerprint is not one the space recorded is checked like any source without an id in common: process ids an
    // import of the space's origin created ask for the pairing (named for the fingerprint), none refuse, and a confirmation backed by
    // them proves it. A confirmation alone proves nothing.
    [Theory]
    [InlineData(true, false, OctopusImportFillEvidenceState.PairingRequired, OctopusImportFillEvidences.ExportFingerprintMismatchBasis, 1)]
    [InlineData(true, true, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ConfirmedPairingBasis, 1)]
    [InlineData(false, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis, 0)]
    [InlineData(false, true, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis, 0)]
    public void AnotherExportFingerprint_CheckedByProcessIds(bool structureMatches, bool confirmed, OctopusImportFillEvidenceState expectedState, string expectedBasis, int expectedMatches)
    {
        var rows = structureMatches ? new[] { ("Project", "Projects-1", (string)null, FingerprintA), ("DeploymentAction", ActionGuid, null, FingerprintA) } : [("Project", "Projects-1", null, FingerprintA)];
        var prior = Prior(null, [FingerprintA], rows);

        var evidence = OctopusImportFillEvidences.Evaluate(null, FingerprintB, [Node("Projects-1", OctopusResourceKind.Project), Node(ActionGuid, OctopusResourceKind.DeploymentAction)], prior, confirmed);

        evidence.State.ShouldBe(expectedState);
        evidence.Basis.ShouldBe(expectedBasis);
        evidence.MatchingStructureIds.ShouldBe(expectedMatches);
        OctopusImportFillEvidences.RefusesLinking(evidence).ShouldBe(expectedState == OctopusImportFillEvidenceState.Unproven);
    }

    // A space may know several fingerprints of its origin (each later one recorded by a confirmed pairing). Each proves its files, and
    // rows any of them created are the origin's: a file of one links to and is backed by rows another created, with no prompt.
    [Theory]
    [InlineData(FingerprintA, FingerprintB, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ExportFingerprintBasis)]
    [InlineData(FingerprintB, FingerprintA, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ExportFingerprintBasis)]
    [InlineData(FingerprintC, FingerprintB, OctopusImportFillEvidenceState.PairingRequired, OctopusImportFillEvidences.ExportFingerprintMismatchBasis)]
    public void KnownFingerprints_EachProvesAndOwnsItsRows(string fileFingerprint, string rowFingerprint, OctopusImportFillEvidenceState expectedState, string expectedBasis)
    {
        var prior = Prior(null, [FingerprintA, FingerprintB], ("Project", "Projects-1", null, rowFingerprint), ("DeploymentAction", ActionGuid, null, rowFingerprint));
        var nodes = new[] { Node("Projects-1", OctopusResourceKind.Project), Node(ActionGuid, OctopusResourceKind.DeploymentAction) };

        var evidence = OctopusImportFillEvidences.Evaluate(null, fileFingerprint, nodes, prior, pairingConfirmed: false);

        evidence.State.ShouldBe(expectedState);
        evidence.Basis.ShouldBe(expectedBasis);
        OctopusImportFillEvidences.MatchingStructureIds(nodes, prior).ShouldBe(1, "a structure row of any known fingerprint backs a pairing");
        OctopusImportFillEvidences.IsKnownExportSource(fileFingerprint, prior).ShouldBe(fileFingerprint != FingerprintC);
    }

    [Theory]
    [InlineData(FingerprintB, true)]
    [InlineData("fedcba9876543210fedcba9876543210", true)]
    [InlineData(FingerprintC, false)]
    [InlineData(null, false)]
    [InlineData(" ", false)]
    public void IsKnownExportSource_AnyRecordedFingerprintComparedCanonically(string fileFingerprint, bool expected)
    {
        OctopusImportFillEvidences.IsKnownExportSource(fileFingerprint, new OctopusImportPriorMappings(null, null, [FingerprintA, FingerprintB])).ShouldBe(expected);
        OctopusImportFillEvidences.IsKnownExportSource(fileFingerprint, OctopusImportPriorMappings.Empty).ShouldBeFalse();
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

    // Child source ids are owner-qualified ("Projects-1/action/<guid>"): the GUID after the last '/' is what proves the origin, and a
    // project and its clone (one GUID, two qualified ids) count it once.
    [Fact]
    public void MatchingStructureIds_QualifiedIds_CountGuids()
    {
        var prior = Prior(null, [], ("DeploymentAction", $"Projects-1/action/{ActionGuid}"));

        OctopusImportFillEvidences.MatchingStructureIds([Node($"Projects-1/action/{ActionGuid}", OctopusResourceKind.DeploymentAction)], prior).ShouldBe(1);
    }

    [Fact]
    public void MatchingStructureIds_ProjectAndClone_CountsDistinctGuids()
    {
        var prior = Prior(null, [], ("DeploymentAction", $"Projects-1/action/{ActionGuid}"), ("DeploymentAction", $"Projects-2/action/{ActionGuid}"));
        var nodes = new[] { Node($"Projects-1/action/{ActionGuid}", OctopusResourceKind.DeploymentAction), Node($"Projects-2/action/{ActionGuid}", OctopusResourceKind.DeploymentAction) };

        OctopusImportFillEvidences.MatchingStructureIds(nodes, prior).ShouldBe(1);
    }

    private static OctopusResourceNode Node(string id, OctopusResourceKind kind)
        => new(id, id, kind, OctopusDocumentKind.Unknown, $"{id}.json", null, null, false, new object());

    private static OctopusImportPriorMappings Prior(string installationId, IReadOnlyList<string> exportSourceIds, params (string Type, string Id)[] rows)
        => Prior(installationId, exportSourceIds, rows.Select(r => (r.Type, r.Id, (string)null, (string)null)).ToArray());

    private static OctopusImportPriorMappings Prior(string installationId, IReadOnlyList<string> exportSourceIds, params (string Type, string Id, string CreatedInstallationId, string CreatedExportSourceId)[] rows)
        => new(rows.ToDictionary(r => OctopusImportPriorMappings.Key(r.Type, r.Id), r => new OctopusImportPriorMapping(900, OctopusImportSourceMapQueries.CreatedOutcome, null, null, r.CreatedInstallationId, r.CreatedExportSourceId)), installationId, exportSourceIds);
}
