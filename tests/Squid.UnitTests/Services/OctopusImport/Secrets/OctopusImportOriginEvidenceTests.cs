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
    public void HitsEarlierImport_OnlyALiveRowOfTheSameKindAndId(string nodeKind, string nodeId, string rowType, string rowId, bool expected)
    {
        var prior = Prior(null, [], (rowType, rowId));

        OctopusImportFillEvidences.HitsEarlierImport([Node(nodeId, Enum.Parse<OctopusResourceKind>(nodeKind))], prior).ShouldBe(expected);
    }

    [Theory]
    [InlineData("inst-a", null, true, true)]
    [InlineData(null, FingerprintA, true, true)]
    [InlineData("inst-a", FingerprintA, true, true)]
    [InlineData("inst-a", null, false, false)]   // a first import, or one with no id in common
    [InlineData(null, null, true, false)]        // no origin recorded: nothing to contradict
    public void NeedsOriginProof_OnlyWhenTheSourceLinksToRowsOfARecordedOrigin(string recordedInstallation, string recordedFingerprint, bool overlapping, bool expected)
    {
        var prior = Prior(recordedInstallation, recordedFingerprint == null ? [] : [recordedFingerprint], ("Project", "Projects-1"));

        OctopusImportFillEvidences.NeedsOriginProof([Node(overlapping ? "Projects-1" : "Projects-9", OctopusResourceKind.Project)], prior).ShouldBe(expected);
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
        OctopusImportFillEvidences.IsAnotherInstallation(incoming, Prior(recorded, [])).ShouldBe(expected);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static OctopusResourceNode Node(string id, OctopusResourceKind kind)
        => new(id, id, kind, OctopusDocumentKind.Unknown, $"{id}.json", null, null, false, new object());

    private static OctopusImportPriorMappings Prior(string installationId, IReadOnlyList<string> exportSourceIds, params (string Type, string Id)[] rows)
        => new(rows.ToDictionary(r => OctopusImportPriorMappings.Key(r.Type, r.Id), _ => new OctopusImportPriorMapping(900, OctopusImportSourceMapQueries.CreatedOutcome, null, null)), installationId, exportSourceIds);
}
