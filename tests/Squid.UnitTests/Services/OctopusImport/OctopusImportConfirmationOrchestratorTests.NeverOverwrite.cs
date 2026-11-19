using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// An update never rolls imported history back: a plan previewed before a later import moved the record on is stale, an older copy of a
/// finished task is kept out by the importer, and history whose Squid owner was re-created moves onto it.
/// </summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    [Theory]
    [InlineData(false, OctopusImportSessionState.Succeeded)]   // the record is what the preview saw → written
    [InlineData(true, OctopusImportSessionState.Failed)]       // a later import moved it on → stale, nothing written
    public async Task ReleaseUpdate_RecordMovedOnSinceThePreview_FailsStale_NothingWritten(bool movedOn, OctopusImportSessionState expected)
    {
        var history = HistoryScenario();
        var preview = Plan((history.Release, OctopusImportPreviewAction.Update, 4001));
        preview.Resources.Single().PriorImportFingerprint = movedOn ? "older-notes|" : OctopusImportPriorMappingPolicy.ImportBasis(PriorOf(history, history.Release));
        var harness = CreateHarness(history.Nodes, BuildDependencyPlan([history.Release]), preview, true, false, false, sourceMap: SourceMap(history.Prior).Object);
        SquidReleaseNotes(harness, ImportedNotes, written: true);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(expected);
        harness.DeploymentHistoryImporter.Verify(i => i.UpdateReleaseNotesIfUnchangedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), movedOn ? Times.Never() : Times.Once());

        if (movedOn) Outcome(harness, history.Release).Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
    }

    [Theory]
    [InlineData(false, OctopusImportSessionState.Succeeded)]
    [InlineData(true, OctopusImportSessionState.Failed)]
    public async Task HistoryUnitUpdate_RecordMovedOnSinceThePreview_FailsStale_NothingWritten(bool movedOn, OctopusImportSessionState expected)
    {
        var history = HistoryScenario(storedTaskFingerprint: "changed-in-octopus");
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Update, 5001, 6001, [], CaptureUnits(out var units));
        harness.Request.PreviewPlan.Resources.Single(r => r.SourceId == history.Deployment.SourceId).PriorImportFingerprint = movedOn ? "previewed-task|" : OctopusImportPriorMappingPolicy.ImportBasis(PriorOf(history, history.Deployment));

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(expected);
        units.Count.ShouldBe(movedOn ? 0 : 1);
    }

    [Fact]
    public async Task HistoryUnitUpdate_ImporterKeptTheNewerTask_UnitStaysReused_StoredFingerprintKept()
    {
        var history = HistoryScenario(storedTaskFingerprint: "finished-task");
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Update, 5001, 6001, upserted, importer => importer.Setup(i => i.UpsertAsync(It.IsAny<OctopusImportHistoryUnit>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportDeploymentHistoryResult(5001, 6001, 8888) { OlderCopyKept = true }));

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        Outcome(harness, history.Deployment).ShouldSatisfyAllConditions(
            o => o.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused),
            o => o.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.OlderHistoryKept));
        Outcome(harness, history.Task).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
        upserted.Where(r => r.SourceType == "Deployment").ShouldAllBe(r => r.Fingerprint == null, "an older copy records nothing, so the newer stored fingerprint stays");
    }

    // The release a Squid delete left without its channel (or project) is pointed at the owners this import resolves.
    [Fact]
    public async Task ReleaseUpdate_PointsTheReleaseAtItsResolvedOwners()
    {
        var history = HistoryScenario();
        var harness = CreateHarness(history.Nodes, BuildDependencyPlan([history.Release]), Plan((history.Release, OctopusImportPreviewAction.Update, 4001)), true, false, false, sourceMap: SourceMap(history.Prior).Object);
        SquidReleaseNotes(harness, ImportedNotes, written: true);

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        harness.DeploymentHistoryImporter.Verify(i => i.RelinkReleaseAsync(4001, 1001, 2001, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static OctopusImportPriorMapping PriorOf(HistoryNodes history, OctopusResourceNode node)
        => history.Prior.TryGet(node.Kind.ToString(), node.SourceId, out var mapping) ? mapping : null;
}
