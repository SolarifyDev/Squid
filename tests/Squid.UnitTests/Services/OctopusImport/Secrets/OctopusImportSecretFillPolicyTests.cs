using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// The pure fill policy shared by preview, validation and confirmation: installation evidence (never names) and how a still-missing
/// secret's row is settled without a write. Every combination is listed, so a change of any rule turns a row red.
/// </summary>
public class OctopusImportSecretFillPolicyTests
{
    private const string ActionGuid = "7a1d0c3e-0000-4000-8000-000000000001";
    private const string PhaseGuid = "7a1d0c3e-0000-4000-8000-000000000002";
    private const string FingerprintA = "0123456789ABCDEF0123456789ABCDEF";
    private const string FingerprintB = "FEDCBA9876543210FEDCBA9876543210";

    // ── Installation evidence ──────────────────────────────────────────────

    [Theory]
    [InlineData("inst-a", null, null, false, false, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.InstallationBasis)]
    [InlineData("INST-A", null, null, false, false, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.InstallationBasis)]
    [InlineData("inst-b", null, null, false, false, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.InstallationMismatchBasis)]
    [InlineData("inst-b", FingerprintA, FingerprintA, true, true, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.InstallationMismatchBasis)] // another installation id outranks everything
    [InlineData(null, FingerprintA, FingerprintA, false, false, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ExportFingerprintBasis)]
    [InlineData(null, "0123456789abcdef0123456789abcdef", FingerprintA, false, false, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ExportFingerprintBasis)]
    [InlineData(null, FingerprintB, FingerprintA, true, true, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis)]
    [InlineData(null, FingerprintA, null, true, false, OctopusImportFillEvidenceState.PairingRequired, null)]
    [InlineData(null, FingerprintA, null, true, true, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ConfirmedPairingBasis)]
    [InlineData(null, null, FingerprintA, true, false, OctopusImportFillEvidenceState.PairingRequired, null)]   // an unsuffixed file proves nothing by fingerprint
    [InlineData(null, null, null, false, true, OctopusImportFillEvidenceState.Unproven, null)]                 // a confirmation alone never proves
    [InlineData(null, FingerprintA, null, false, true, OctopusImportFillEvidenceState.Unproven, null)]
    public void Evidence_FromInstallationFingerprintOrConfirmedStructureIds(string fileInstallation, string fileFingerprint, string recordedFingerprint, bool structureCreatedBefore, bool confirmed, OctopusImportFillEvidenceState expected, string expectedBasis)
    {
        var prior = Prior("inst-a", recordedFingerprint == null ? [] : [recordedFingerprint], structureCreatedBefore ? OctopusImportSourceMapQueries.CreatedOutcome : null);

        var evidence = OctopusImportFillEvidences.Evaluate(fileInstallation, fileFingerprint, StructureNodes(), prior, confirmed);

        evidence.State.ShouldBe(expected);
        evidence.Basis.ShouldBe(expectedBasis);
    }

    // Only the fingerprint the space's FIRST export-file import recorded proves a file (D10). A space may carry more than one (rows
    // recorded before this rule, or by a file from a cloned server): those later ones prove nothing, whatever the pairing says.
    [Theory]
    [InlineData(FingerprintA, OctopusImportFillEvidenceState.Proven, OctopusImportFillEvidences.ExportFingerprintBasis)]
    [InlineData(FingerprintB, OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", OctopusImportFillEvidenceState.Unproven, OctopusImportFillEvidences.ExportFingerprintMismatchBasis)]
    public void Evidence_OnlyTheFirstRecordedFingerprintProves(string fileFingerprint, OctopusImportFillEvidenceState expected, string expectedBasis)
    {
        var prior = Prior("inst-a", [FingerprintA, FingerprintB], OctopusImportSourceMapQueries.CreatedOutcome);

        var evidence = OctopusImportFillEvidences.Evaluate(null, fileFingerprint, StructureNodes(), prior, pairingConfirmed: true);

        evidence.State.ShouldBe(expected);
        evidence.Basis.ShouldBe(expectedBasis);
    }

    // ── Fill destinations ──────────────────────────────────────────────────

    // Only the owners of a LIVE Missing row are named: the project / library variable set by its variable set, the feed by its id.
    [Fact]
    public void FillDestinations_OnlyOwnersOfLiveMissingRows()
    {
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>
        {
            [(OctopusImportSourceMapQueries.VariableSecretSourceType, "VS:1")] = new(601, OctopusImportSourceMapQueries.MissingOutcome, null, null),
            [(OctopusImportSourceMapQueries.VariableSecretSourceType, "VS:2")] = new(602, OctopusImportSourceMapQueries.MissingOutcome, null, null),
            [(OctopusImportSourceMapQueries.VariableSecretSourceType, "VS:3")] = new(603, OctopusImportSourceMapQueries.FilledOutcome, null, null),
            [(OctopusImportSourceMapQueries.FeedSecretSourceType, "FEEDS-1")] = new(801, OctopusImportSourceMapQueries.MissingOutcome, null, null)
        }, "inst-a");
        OctopusImportDestinationResource Resource(OctopusResourceKind kind, int id, string name, int? variableSetId = null) => new(id, 1, kind, name, null, DateTimeOffset.UtcNow) { VariableSetId = variableSetId };

        var destinations = OctopusImportFillDestinations.From([Resource(OctopusResourceKind.LibraryVariableSet, 801, "Library with the feed's id", 602), Resource(OctopusResourceKind.Project, 501, "Web", 601), Resource(OctopusResourceKind.LibraryVariableSet, 71, "Shared", 602), Resource(OctopusResourceKind.Project, 503, "Settled", 603), Resource(OctopusResourceKind.Feed, 801, "Registry"), Resource(OctopusResourceKind.Feed, 802, "Other"), Resource(OctopusResourceKind.Environment, 601, "Same id, not an owner")], prior);

        destinations.Select(d => (d.OwnerKind, d.OwnerId, d.OwnerName, d.VariableSetId)).ShouldBe([("LibraryVariableSet", 801, "Library with the feed's id", 602), ("Project", 501, "Web", 601), ("LibraryVariableSet", 71, "Shared", 602), ("Feed", 801, "Registry", (int?)null)], ignoreOrder: true);

        var feed = new OctopusImportSecretFillCandidate(OctopusResourceKind.Feed, OctopusImportSourceMapQueries.FeedSecretSourceType, "Feeds-1", 601, null, null, OctopusImportSecretFillSource.Fillable);
        OctopusImportFillDestinations.Of(feed, destinations).ShouldBeNull("a feed never matches a variable set with the same number");
        OctopusImportFillDestinations.Of(feed with { DestinationId = 801 }, destinations).OwnerName.ShouldBe("Registry");
    }

    // Only GUID step/action/phase ids an earlier import CREATED count: reused or skipped rows, non-GUID ids and history do not.
    [Theory]
    [InlineData(OctopusImportSourceMapQueries.CreatedOutcome, 2)]
    [InlineData(OctopusImportSourceMapQueries.ReusedOutcome, 0)]
    [InlineData(null, 0)]
    public void StructureIds_OnlyCreatedGuidsOfCurrentConfigurationCount(string outcome, int expected)
    {
        var prior = Prior("inst-a", [], outcome, extraCreated: [(nameof(OctopusResourceKind.DeploymentStep), "Projects-1-step-1"), (nameof(OctopusResourceKind.Environment), "6c1a2b3d-0000-4000-8000-000000000009")]);
        var nodes = StructureNodes().Concat([Node("Projects-1-step-1", OctopusResourceKind.DeploymentStep), Node("6c1a2b3d-0000-4000-8000-000000000009", OctopusResourceKind.Environment), Node(ActionGuid, OctopusResourceKind.DeploymentAction, historical: true)]);

        OctopusImportFillEvidences.MatchingStructureIds(nodes, prior).ShouldBe(expected);
    }

    // ── Settling a row without a write ─────────────────────────────────────

    [Theory]
    [InlineData(OctopusImportSecretFillSource.Fillable, SecretSlotState.AlreadySet, OctopusImportFillEvidenceState.Unproven, OctopusImportSourceMapQueries.SupersededOutcome)]
    [InlineData(OctopusImportSecretFillSource.StillMissing, SecretSlotState.AlreadySet, OctopusImportFillEvidenceState.Unproven, OctopusImportSourceMapQueries.SupersededOutcome)]
    [InlineData(OctopusImportSecretFillSource.Ambiguous, SecretSlotState.NotFound, OctopusImportFillEvidenceState.Unproven, OctopusImportSourceMapQueries.SupersededOutcome)]
    [InlineData(OctopusImportSecretFillSource.EmptyInOctopus, SecretSlotState.Empty, OctopusImportFillEvidenceState.Proven, OctopusImportSourceMapQueries.FilledOutcome)]
    [InlineData(OctopusImportSecretFillSource.EmptyInOctopus, SecretSlotState.Empty, OctopusImportFillEvidenceState.PairingRequired, null)]
    [InlineData(OctopusImportSecretFillSource.EmptyInOctopus, SecretSlotState.Empty, OctopusImportFillEvidenceState.Unproven, null)]
    [InlineData(OctopusImportSecretFillSource.EmptyInOctopus, SecretSlotState.Ambiguous, OctopusImportFillEvidenceState.Proven, null)]
    [InlineData(OctopusImportSecretFillSource.Fillable, SecretSlotState.Empty, OctopusImportFillEvidenceState.Proven, null)]   // a fill, not a settle
    [InlineData(OctopusImportSecretFillSource.StillMissing, SecretSlotState.Empty, OctopusImportFillEvidenceState.Proven, null)]
    [InlineData(OctopusImportSecretFillSource.Fillable, SecretSlotState.Ambiguous, OctopusImportFillEvidenceState.Proven, null)]
    public void SettledWithoutWrite_SupersededOnceSquidHoldsAValue_FilledOnlyWhenAProvenFileSaysEmpty(OctopusImportSecretFillSource source, SecretSlotState destination, OctopusImportFillEvidenceState evidence, string expected)
    {
        var candidate = new OctopusImportSecretFillCandidate(OctopusResourceKind.Variable, OctopusImportSourceMapQueries.VariableSecretSourceType, "vs:x", 601, VariableIdentity.Of("Db.Password", VariableType.Password, []), null, source);

        OctopusImportSecretFills.SettledWithoutWrite(candidate, destination, evidence).ShouldBe(expected);
        OctopusImportSecretFills.Fills(candidate, destination, evidence).ShouldBe(source == OctopusImportSecretFillSource.Fillable && destination == SecretSlotState.Empty && evidence == OctopusImportFillEvidenceState.Proven);
    }

    // Only a LIVE Missing row is a candidate: Filled and Superseded are terminal.
    [Theory]
    [InlineData(OctopusImportSourceMapQueries.MissingOutcome, true)]
    [InlineData(OctopusImportSourceMapQueries.FilledOutcome, false)]
    [InlineData(OctopusImportSourceMapQueries.SupersededOutcome, false)]
    public void FeedCandidate_OnlyForALiveMissingRow(string outcome, bool expected)
    {
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [OctopusImportPriorMappings.Key(OctopusImportSourceMapQueries.FeedSecretSourceType, "Feeds-1")] = new(801, outcome, null, null) }, "inst-a");
        var feed = new OctopusResourceNode("Feeds-1", "Registry", OctopusResourceKind.Feed, OctopusDocumentKind.Feed, "feed.json", null, null, false, new OctopusFeedDto { Id = "Feeds-1", Username = "robot", Password = null });

        (OctopusImportSecretFills.Candidate(feed, prior, OctopusExportSecrets.FromPassword("pw"), null) != null).ShouldBe(expected);
        OctopusImportSecretFills.MissingDestinations(prior, OctopusImportSourceMapQueries.FeedSecretSourceType).ShouldBe(expected ? [801] : []);
    }

    // A source without export secrets (an Octopus server, or a file without its password) never speaks for a feed password — not even
    // as "empty in Octopus", which would settle the row and stop every later fill.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FeedSource_WithoutExportSecrets_StillMissing(string password)
    {
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [OctopusImportPriorMappings.Key(OctopusImportSourceMapQueries.FeedSecretSourceType, "Feeds-1")] = new(801, OctopusImportSourceMapQueries.MissingOutcome, null, null) }, "inst-a");
        var feed = new OctopusResourceNode("Feeds-1", "Registry", OctopusResourceKind.Feed, OctopusDocumentKind.Feed, "feed.json", null, null, false, new OctopusFeedDto { Id = "Feeds-1", Username = "robot", Password = password });

        OctopusImportSecretFills.Candidate(feed, prior, null, null).Source.ShouldBe(OctopusImportSecretFillSource.StillMissing);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IEnumerable<OctopusResourceNode> StructureNodes() => [Node(ActionGuid, OctopusResourceKind.DeploymentAction), Node(PhaseGuid, OctopusResourceKind.LifecyclePhase)];

    private static OctopusResourceNode Node(string id, OctopusResourceKind kind, bool historical = false)
        => new(id, id, kind, OctopusDocumentKind.DeploymentProcess, $"{id}.json", null, null, historical, new object());

    private static OctopusImportPriorMappings Prior(string installationId, IReadOnlyList<string> exportSourceIds, string structureOutcome, IEnumerable<(string Type, string Id)> extraCreated = null)
    {
        var rows = new Dictionary<(string Type, string Id), OctopusImportPriorMapping>();

        if (structureOutcome != null)
        {
            rows[OctopusImportPriorMappings.Key(nameof(OctopusResourceKind.DeploymentAction), ActionGuid)] = new(701, structureOutcome, null, null);
            rows[OctopusImportPriorMappings.Key(nameof(OctopusResourceKind.LifecyclePhase), PhaseGuid)] = new(702, structureOutcome, null, null);
        }

        foreach (var (type, id) in extraCreated ?? [])
            rows[OctopusImportPriorMappings.Key(type, id)] = new(900, OctopusImportSourceMapQueries.CreatedOutcome, null, null);

        return new OctopusImportPriorMappings(rows, installationId, exportSourceIds);
    }
}
