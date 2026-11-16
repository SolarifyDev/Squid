using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The import's origin re-checked under the space lock against the RELOADED prior mappings: validation ran before the lock (another
/// import may have recorded an origin since), and the posted plan is the client's (a pairing confirmation alone proves nothing).
/// </summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    private const string OriginActionGuid = "5e0c7a11-0000-4000-8000-0000000000b1";
    private const string OriginFingerprint = "0123456789ABCDEF0123456789ABCDEF";
    private const string OtherFingerprint = "FEDCBA9876543210FEDCBA9876543210";

    [Theory]
    [InlineData("inst-a", null, "inst-b", null, true, true, false)]                          // another installation, whatever is confirmed
    [InlineData("inst-a", null, null, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false, true, false)] // a file with no process id in common
    [InlineData(null, OriginFingerprint, "inst-x", null, true, false, false)]                 // GUIDs match, pairing not confirmed (plan forged / stale)
    [InlineData(null, OriginFingerprint, "inst-x", null, true, true, true)]                   // confirmed pairing backed by a GUID
    [InlineData(null, OriginFingerprint, "inst-x", null, false, true, false)]                 // a confirmation without matching GUIDs proves nothing
    [InlineData(null, OriginFingerprint, null, OtherFingerprint, false, false, false)]       // another fingerprint, no process id in common
    [InlineData(null, OriginFingerprint, null, OtherFingerprint, false, true, false)]        // ... a confirmation alone proves nothing
    [InlineData(null, OriginFingerprint, null, OtherFingerprint, true, false, false)]        // ... matching GUIDs, pairing not confirmed
    [InlineData(null, OriginFingerprint, null, OtherFingerprint, true, true, true)]          // ... confirmed pairing backed by a GUID
    [InlineData(null, OriginFingerprint, null, OriginFingerprint, false, false, true)]       // proven by the recorded fingerprint
    [InlineData("inst-a", null, "inst-a", null, false, false, true)]                         // proven by installation
    [InlineData(null, null, "inst-x", null, false, false, false)]                            // no origin recorded, rows of unknown origin: no evidence
    [InlineData(null, null, "inst-x", null, true, false, false)]                             // ... matching GUIDs still need the confirmation
    [InlineData(null, null, "inst-x", null, true, true, true)]                               // ... and are imported once it is confirmed
    public async Task Origin_ReCheckedUnderTheSpaceLock(string recordedInstallation, string recordedFingerprint, string sourceInstallation, string sourceFingerprint, bool structureMatches, bool confirmed, bool expectSucceeded)
    {
        var nodes = BuildMinimalNodes();
        var action = new OctopusResourceNode(OriginActionGuid, "Run", OctopusResourceKind.DeploymentAction, OctopusDocumentKind.DeploymentProcess, "process.json", null, null, false, new object());
        var upserted = new List<OctopusImportSourceMapRow>();
        var preview = Plan((nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 321));
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), preview, true, false, false, sourceMap: SourceMap(OriginPrior(nodes.Environment, action, recordedInstallation, recordedFingerprint, structureMatches), upserted: upserted).Object);
        var request = harness.Request with { Graph = new OctopusResourceGraph([nodes.Environment, action], [], [], []) { InstallationId = sourceInstallation, ExportSourceId = sourceFingerprint } };

        if (confirmed) request.PreviewPlan.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(expectSucceeded ? OctopusImportSessionState.Succeeded : OctopusImportSessionState.Failed);
        harness.SessionService.RecordedResult.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven && d.Severity == OctopusImportCompatibilitySeverity.Blocker).ShouldBe(!expectSucceeded);
        upserted.ShouldBe(expectSucceeded ? upserted : [], "a refused import links and records nothing");
    }

    // No id of the source is in the space, so nothing needs proof — but the installation is recorded only where it cannot vouch for
    // rows another origin created: in an empty space, or when it is the space's origin. Recorded next to another server's fingerprint
    // (or over rows of unknown origin), it would prove that server's later imports by installation, and a later colliding id (its
    // Environments-99 against the file's Environments-99) would link without any check. Another installation recorded since validation
    // is refused under the lock.
    [Theory]
    [InlineData(null, null, "inst-x", false, true, true)]               // an empty space: the first import is its origin
    [InlineData(null, null, "inst-x", true, true, false)]               // rows of unknown origin: imported, but not adopted
    [InlineData("inst-x", null, "inst-x", true, true, true)]            // the recorded installation itself
    [InlineData(null, OriginFingerprint, "inst-x", true, true, false)]  // a file space: imported, but the server is not its origin
    [InlineData("inst-a", null, "inst-b", true, false, false)]          // another installation recorded between validation and the lock
    public async Task Origin_NoIdInCommon_InstallationRecordedOnlyWhereItIsTheOrigin(string recordedInstallation, string recordedFingerprint, string sourceInstallation, bool spaceHasRows, bool expectSucceeded, bool expectRecorded)
    {
        var nodes = BuildMinimalNodes();
        var upserted = new List<OctopusImportSourceMapRow>();
        var unrelated = new Dictionary<(string Type, string Id), OctopusImportPriorMapping>();

        if (spaceHasRows) unrelated[OctopusImportPriorMappings.Key(nameof(OctopusResourceKind.Environment), "Environments-99")] = new(99, "Created", null, null);

        var prior = new OctopusImportPriorMappings(unrelated, recordedInstallation, recordedFingerprint == null ? [] : [recordedFingerprint]);
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.ReuseExisting, 200)), true, false, false, sourceMap: SourceMap(prior, upserted: upserted).Object);
        var request = harness.Request with { Graph = harness.Request.Graph with { InstallationId = sourceInstallation } };

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(expectSucceeded ? OctopusImportSessionState.Succeeded : OctopusImportSessionState.Failed);
        harness.SessionService.RecordedResult.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).ShouldBe(!expectSucceeded);
        upserted.Any(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType && r.SourceId == sourceInstallation).ShouldBe(expectRecorded);
    }

    // After a confirmed pairing both origins are recorded, so the next import of either is proven by id without asking.
    [Fact]
    public async Task ConfirmedServerIntoAFileSpace_RecordsTheInstallationNextToThePinnedFingerprint()
    {
        var nodes = BuildMinimalNodes();
        var action = new OctopusResourceNode(OriginActionGuid, "Run", OctopusResourceKind.DeploymentAction, OctopusDocumentKind.DeploymentProcess, "process.json", null, null, false, new object());
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 321)), true, false, false, sourceMap: SourceMap(OriginPrior(nodes.Environment, action, null, OriginFingerprint, true), upserted: upserted).Object);
        var request = harness.Request with { Graph = new OctopusResourceGraph([nodes.Environment, action], [], [], []) { InstallationId = "inst-x" } };
        request.PreviewPlan.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };

        (await harness.Sut.ConfirmAsync(request, CancellationToken.None)).State.ShouldBe(OctopusImportSessionState.Succeeded);

        upserted.ShouldContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType && r.SourceId == "inst-x");
    }

    // A file of another fingerprint whose pairing the user confirmed (backed by the origin's GUIDs) is another export of the space's
    // Octopus: its fingerprint is recorded next to the pinned one, so its next import is proven without asking. Unconfirmed, it is
    // refused and records nothing; a file the space already knows records no second row.
    [Theory]
    [InlineData(OtherFingerprint, true, true)]
    [InlineData(OtherFingerprint, false, false)]
    [InlineData(OriginFingerprint, false, false)]
    public async Task ConfirmedFileOfAnotherFingerprint_RecordedAsAnotherFingerprintOfTheOrigin(string fileFingerprint, bool confirmed, bool expectRecorded)
    {
        var nodes = BuildMinimalNodes();
        var action = new OctopusResourceNode(OriginActionGuid, "Run", OctopusResourceKind.DeploymentAction, OctopusDocumentKind.DeploymentProcess, "process.json", null, null, false, new object());
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 321)), true, false, false, sourceMap: SourceMap(OriginPrior(nodes.Environment, action, null, OriginFingerprint, true), upserted: upserted).Object);
        var request = harness.Request with { Graph = new OctopusResourceGraph([nodes.Environment, action], [], [], []) { ExportSourceId = fileFingerprint } };

        if (confirmed) request.PreviewPlan.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(fileFingerprint == OriginFingerprint || confirmed ? OctopusImportSessionState.Succeeded : OctopusImportSessionState.Failed);
        upserted.Where(r => r.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType).Select(r => r.SourceId).ShouldBe(expectRecorded ? [OtherFingerprint] : []);
        upserted.Where(r => r.SourceType != OctopusImportSourceMapQueries.ExportSourceSourceType).ShouldAllBe(r => r.CreatedExportSourceId == fileFingerprint, "every row it writes names the file's fingerprint");
    }

    // Every row a confirmation writes names its origin, so a later import can tell its own rows from another origin's.
    [Fact]
    public async Task EveryWrittenRow_NamesTheImportsOrigin()
    {
        var nodes = BuildMinimalNodes();
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.ReuseExisting, 200)), true, false, false, sourceMap: SourceMap(OctopusImportPriorMappings.Empty, upserted: upserted).Object);
        var request = harness.Request with { Graph = harness.Request.Graph with { InstallationId = "inst-x", ExportSourceId = OriginFingerprint } };

        (await harness.Sut.ConfirmAsync(request, CancellationToken.None)).State.ShouldBe(OctopusImportSessionState.Succeeded);

        upserted.ShouldNotBeEmpty();
        upserted.ShouldAllBe(r => r.CreatedInstallationId == "inst-x" && r.CreatedExportSourceId == OriginFingerprint);
    }

    // The space's origin is a file (Y); server X was imported earlier with no id in common, so its rows name X and X is not the origin.
    // X re-imported meets only its own rows: no proof needed. X meeting a row of Y's needs a pairing backed by Y's structure rows — its
    // own GUIDs prove nothing, so a confirmation backed only by them is refused (it would make X the origin of Y's rows).
    [Theory]
    [InlineData(false, false, true)]  // only X's own rows
    [InlineData(true, true, false)]   // a colliding id of Y's, "confirmed" with X's own GUIDs only
    [InlineData(true, false, false)]
    public async Task Origin_RowsTheSourceCreatedItself_LinkFreely_NeverBackAPairing(bool hitsOriginRow, bool confirmed, bool expectSucceeded)
    {
        var nodes = BuildMinimalNodes();
        var action = new OctopusResourceNode(OriginActionGuid, "Run", OctopusResourceKind.DeploymentAction, OctopusDocumentKind.DeploymentProcess, "process.json", null, null, false, new object());
        var upserted = new List<OctopusImportSourceMapRow>();
        var rows = new Dictionary<(string Type, string Id), OctopusImportPriorMapping>
        {
            [OctopusImportPriorMappings.Key(nodes.Environment.Kind.ToString(), nodes.Environment.SourceId)] = new(321, "Created", null, null, hitsOriginRow ? null : "inst-x", hitsOriginRow ? OriginFingerprint : null),
            [OctopusImportPriorMappings.Key(action.Kind.ToString(), action.SourceId)] = new(701, "Created", null, null, "inst-x")
        };
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 321)), true, false, false, sourceMap: SourceMap(new OctopusImportPriorMappings(rows, null, [OriginFingerprint]), upserted: upserted).Object);
        var request = harness.Request with { Graph = new OctopusResourceGraph([nodes.Environment, action], [], [], []) { InstallationId = "inst-x" } };

        if (confirmed) request.PreviewPlan.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(expectSucceeded ? OctopusImportSessionState.Succeeded : OctopusImportSessionState.Failed);
        upserted.ShouldNotContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType, "X is never adopted as the origin of Y's space");
    }

    private static OctopusImportPriorMappings OriginPrior(OctopusResourceNode environment, OctopusResourceNode action, string installationId, string fingerprint, bool structureMatches)
    {
        var rows = new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [OctopusImportPriorMappings.Key(environment.Kind.ToString(), environment.SourceId)] = new(321, "Created", null, null) };

        if (structureMatches) rows[OctopusImportPriorMappings.Key(action.Kind.ToString(), action.SourceId)] = new(701, "Created", null, null);

        return new OctopusImportPriorMappings(rows, installationId, fingerprint == null ? [] : [fingerprint]);
    }
}
