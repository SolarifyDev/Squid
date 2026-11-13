using System.Linq;
using Mediator.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>Confirmation against the persistent source map: space lock, reloaded prior mappings, stale checks, history units and the map upsert.</summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    private const string InstallationId = "8a3f1c2e-0000-4000-8000-000000000001";

    [Fact]
    public async Task SpaceLock_IsTakenBeforeThePriorMappingsAreReloaded()
    {
        var nodes = BuildMinimalNodes();
        var calls = new List<string>();
        var sourceMap = SourceMap(OctopusImportPriorMappings.Empty, calls);
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), BuildPreviewPlan([nodes.Environment], reusedResources: [nodes.Environment]), true, false, false, sourceMap: sourceMap.Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        calls.ShouldBe(["lock:7", "load:7", "upsert"]);
    }

    [Fact]
    public async Task AlreadyImported_BackedByMatchingPriorHit_IsReusedWithItsMapping()
    {
        var nodes = BuildMinimalNodes();
        var preview = Plan((nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 321));
        var sourceMap = SourceMap(Prior((nodes.Environment, 321)));
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), preview, true, false, false, sourceMap: sourceMap.Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        Outcome(harness, nodes.Environment).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
        harness.SessionService.RecordedResult.IdMappings.ShouldHaveSingleItem().DestinationId.ShouldBe(321);
        harness.Mediator.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task AlreadyImportedChild_UnderVerifiedOwner_IsReusedWithoutAMapping()
    {
        var nodes = BuildMinimalNodes();
        var channel = Node("Channels-9", OctopusResourceKind.Channel, OctopusDocumentKind.Channel, "Hotfix", new OctopusChannelDto { Id = "Channels-9", Name = "Hotfix", ProjectId = "Projects-1" }, nodes.Project.SourceId);
        var preview = Plan((nodes.Project, OctopusImportPreviewAction.AlreadyImported, 1001), (channel, OctopusImportPreviewAction.AlreadyImported, null));
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Project, channel]), preview, true, false, false, sourceMap: SourceMap(Prior((nodes.Project, 1001))).Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        Outcome(harness, channel).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
        harness.SessionService.RecordedResult.IdMappings.ShouldHaveSingleItem().SourceId.ShouldBe(nodes.Project.SourceId);
    }

    [Fact]
    public async Task Create_WhoseSourceWasImportedSinceThePreview_FailsStaleWithoutWriting()
    {
        var nodes = BuildMinimalNodes();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), BuildPreviewPlan([nodes.Environment]), true, false, false, sourceMap: SourceMap(Prior((nodes.Environment, 55))).Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        Outcome(harness, nodes.Environment).Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan && d.Message.Contains("Re-run the preview"));
        harness.Mediator.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task SourceMap_RecordsEveryMappingWithConfigFingerprints_AndTheInstallation()
    {
        var nodes = BuildMinimalNodes();
        ((OctopusDocumentDto)nodes.Environment.Source).DataVersion = "dv-1";
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment, nodes.ProjectGroup]), Plan((nodes.Environment, OctopusImportPreviewAction.ReuseExisting, 200), (nodes.ProjectGroup, OctopusImportPreviewAction.Create, null)), true, false, false, sourceMap: SourceMap(OctopusImportPriorMappings.Empty, upserted: upserted).Object);
        var request = harness.Request with { Graph = harness.Request.Graph with { InstallationId = InstallationId } };

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        upserted.ShouldContain(r => r.SourceType == "Environment" && r.SourceId == "Environments-1" && r.DestinationId == 200 && r.OutcomeState == "Reused" && r.Fingerprint == null);
        upserted.ShouldContain(r => r.SourceType == "ProjectGroup" && r.OutcomeState == "Created" && r.DestinationId > 0);
        upserted.ShouldContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType && r.SourceId == InstallationId && r.DestinationId == nodes.DestinationSpaceId && r.OutcomeState == "Reused");
        upserted.ShouldAllBe(r => r.SpaceId == nodes.DestinationSpaceId);
    }

    // The export's id suffix is kept as the space's export-source fingerprint (cross-server evidence for later fills).
    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData(null, false)]
    public async Task SourceMap_RecordsTheExportSourceFingerprint_OnlyForSuffixedFiles(string exportSourceId, bool expected)
    {
        var nodes = BuildMinimalNodes();
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.ReuseExisting, 200)), true, false, false, sourceMap: SourceMap(OctopusImportPriorMappings.Empty, upserted: upserted).Object);
        var request = harness.Request with { Graph = harness.Request.Graph with { ExportSourceId = exportSourceId } };

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        upserted.Any(r => r.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType && r.SourceId == exportSourceId && r.DestinationId == nodes.DestinationSpaceId && r.OutcomeState == "Reused").ShouldBe(expected);
        upserted.Count(r => r.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType).ShouldBe(expected ? 1 : 0);
    }

    [Fact]
    public async Task SourceMap_IsNotWrittenWhenTheConfirmationFails()
    {
        var nodes = BuildMinimalNodes();
        var upserted = new List<OctopusImportSourceMapRow>();
        var sourceMap = SourceMap(Prior((nodes.Environment, 55)), upserted: upserted);
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), BuildPreviewPlan([nodes.Environment]), true, false, false, sourceMap: sourceMap.Object);

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        sourceMap.Verify(s => s.UpsertAsync(It.IsAny<IReadOnlyList<OctopusImportSourceMapRow>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReleaseUpdate_WritesOnlyTheNotes_AndRecordsTheNewFingerprint()
    {
        var history = HistoryScenario();
        var upserted = new List<OctopusImportSourceMapRow>();
        var preview = Plan((history.Release, OctopusImportPreviewAction.Update, 4001));
        var harness = CreateHarness(history.Nodes, BuildDependencyPlan([history.Release]), preview, true, false, false, sourceMap: SourceMap(history.Prior, upserted: upserted).Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.DeploymentHistoryImporter.Verify(i => i.UpdateReleaseNotesAsync(4001, "New notes", It.IsAny<CancellationToken>()), Times.Once);
        Outcome(harness, history.Release).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Updated);
        upserted.ShouldContain(r => r.SourceType == "Release" && r.DestinationId == 4001 && r.Fingerprint == OctopusImportFingerprint.ForRelease(new OctopusReleaseDto { ReleaseNotes = "New notes" }));
    }

    [Theory]
    [InlineData(9999)]   // an id the source map never recorded for this release
    [InlineData(null)]   // an update must name the row it updates
    public async Task Update_NotBackedBySourceMap_FailsStaleWithoutWriting(int? destinationId)
    {
        var history = HistoryScenario();
        var harness = CreateHarness(history.Nodes, BuildDependencyPlan([history.Release]), Plan((history.Release, OctopusImportPreviewAction.Update, destinationId)), true, false, false, sourceMap: SourceMap(history.Prior).Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        Outcome(harness, history.Release).Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
        harness.DeploymentHistoryImporter.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task HistoryUnitCreate_UsesPriorOwners_AndRecordsTheTaskFingerprint()
    {
        var history = HistoryScenario(storeDeployment: false);
        var upserted = new List<OctopusImportSourceMapRow>();
        var captured = CaptureUnits(out var units);
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Create, null, null, upserted, captured);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var unit = units.ShouldHaveSingleItem();
        (unit.ProjectId, unit.ChannelId, unit.ReleaseId, unit.EnvironmentId).ShouldBe((1001, 2001, 4001, 3001));
        (unit.ExistingDeploymentId, unit.ExistingTaskId).ShouldBe((null, null));
        unit.MachineNames["Machines-1"].ShouldBe("web-01");
        Outcome(harness, history.Deployment).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
        Outcome(harness, history.Task).DestinationId.ShouldBe(6001);
        upserted.ShouldContain(r => r.SourceType == "Deployment" && r.DestinationId == 5001 && r.Fingerprint == OctopusImportFingerprint.ForTask(history.TaskDto) && r.DetailFingerprint == null);
        upserted.ShouldContain(r => r.SourceType == "ServerTask" && r.DestinationId == 6001 && r.Fingerprint == null);
    }

    [Fact]
    public async Task HistoryUnitUpdate_RewritesTheStoredRowsInPlace()
    {
        var history = HistoryScenario(storedTaskFingerprint: "changed-in-octopus");
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Update, 5001, 6001, upserted, CaptureUnits(out var units));

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var unit = units.ShouldHaveSingleItem();
        (unit.ExistingDeploymentId, unit.ExistingTaskId, unit.ScalarChanged, unit.ReplaceChildren).ShouldBe((5001, 6001, true, true));
        Outcome(harness, history.Deployment).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Updated);
        Outcome(harness, history.Task).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Updated);
        harness.SessionService.RecordedResult.IdMappings.Where(m => m.SourceType is "Deployment" or "ServerTask").ShouldAllBe(m => m.OutcomeState == OctopusImportResourceOutcomeState.Reused);
        upserted.ShouldContain(r => r.SourceType == "Deployment" && r.Fingerprint == OctopusImportFingerprint.ForTask(history.TaskDto));
    }

    [Fact]
    public async Task HistoryUnitUpdate_WhoseTaskRowIsGone_RecreatesTheTask()
    {
        var history = HistoryScenario(storeTask: false);
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Update, 5001, null, [], CaptureUnits(out var units));

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        units.ShouldHaveSingleItem().ExistingTaskId.ShouldBeNull();
        Outcome(harness, history.Task).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
    }

    [Fact]
    public async Task HistoryUnitUpdate_ReadsStoredDetails_AndRecordsTheirFingerprint()
    {
        var history = HistoryScenario(storedTaskFingerprint: "changed-in-octopus");
        var details = new OctopusTaskHistoryDetails { ActivityLogs = [new OctopusActivityLogDto { Name = "Task" }] };
        var store = new Mock<IOctopusImportHistoryDetailsStore>();
        store.Setup(s => s.ReadAsync("/sessions/abc", "ServerTasks-1", It.IsAny<CancellationToken>())).ReturnsAsync(details);
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Update, 5001, 6001, upserted, CaptureUnits(out var units), store.Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request with { SessionDirectory = "/sessions/abc" }, CancellationToken.None);

        var expectedDetailFingerprint = OctopusImportFingerprint.ForTaskDetails(details);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        units.ShouldHaveSingleItem().Details.ShouldBeSameAs(details);
        upserted.ShouldContain(r => r.SourceType == "Deployment" && r.DetailFingerprint == expectedDetailFingerprint);
    }

    [Fact]
    public async Task ReleaseCreate_IsMarkedAsAHistoricalImport()
    {
        var harness = CreateRichHarness();

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        harness.Mediator.Invocations.Where(i => i.Method.Name == nameof(IMediator.SendAsync)).Select(i => i.Arguments[0]).OfType<Squid.Message.Commands.Deployments.Release.CreateReleaseCommand>().Single().IsHistoricalImport.ShouldBeTrue();
    }

    [Fact]
    public async Task HistoryUnit_WhoseDetailsAreNotInTheStore_RecordsNoDetailFingerprint()
    {
        var history = HistoryScenario(storeDeployment: false);
        history.TaskDto.DetailLevel = 1;
        history.TaskDto.DetailFingerprint = "claimed-by-the-document";
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Create, null, null, upserted, CaptureUnits(out var units), Mock.Of<IOctopusImportHistoryDetailsStore>());

        var result = await harness.Sut.ConfirmAsync(harness.Request with { SessionDirectory = "/sessions/abc" }, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        units.ShouldHaveSingleItem().Details.ShouldBeNull();
        upserted.Single(r => r.SourceType == "Deployment").DetailFingerprint.ShouldBeNull();
    }

    // Only-fill: a changed task whose details the fetch could only partly read keeps its imported children, and the
    // stored task fingerprint stays the old one, so the next fetch sees the task as changed and fetches the details
    // again instead of skipping them as already stored. A summary-level unit (no stored details) has its children
    // replaced at summary level, so its task fingerprint advances; otherwise it would preview as Update on every run.
    [Theory]
    [InlineData("stored-details", true, false, false)]   // details incomplete, children kept → task fingerprint not advanced (null keeps the stored one)
    [InlineData("stored-details", false, false, true)]   // a source simply without details (an export file) → task fingerprint advanced as before
    [InlineData(null, true, true, true)]                 // summary-level unit, details incomplete → children replaced at summary level, fingerprint advanced
    public async Task HistoryUnitUpdate_AdvancesTheTaskFingerprintUnlessIncompleteDetailsKeptTheStoredChildren(string storedDetailFingerprint, bool detailsIncomplete, bool expectedReplaceChildren, bool expectedAdvanced)
    {
        var history = HistoryScenario(storedTaskFingerprint: "changed-in-octopus", storedDetailFingerprint: storedDetailFingerprint);
        history.TaskDto.DetailsIncomplete = detailsIncomplete;
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHistoryHarness(history, OctopusImportPreviewAction.Update, 5001, 6001, upserted, CaptureUnits(out var units));

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var unit = units.ShouldHaveSingleItem();
        (unit.ScalarChanged, unit.ReplaceChildren).ShouldBe((true, expectedReplaceChildren));
        var row = upserted.Single(r => r.SourceType == "Deployment");
        (row.Fingerprint, row.DetailFingerprint, row.DetailsCleared).ShouldBe((expectedAdvanced ? OctopusImportFingerprint.ForTask(history.TaskDto) : null, null, expectedReplaceChildren));
    }

    [Fact]
    public async Task UpdateOfAConfigurationKind_IsStillRejected()
    {
        var nodes = BuildMinimalNodes();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.Update, 321)), true, false, false, sourceMap: SourceMap(Prior((nodes.Environment, 321))).Object);

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        Outcome(harness, nodes.Environment).Diagnostics.ShouldContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.ResourceActionUnsupported);
    }

    private TestHarness CreateHistoryHarness(HistoryNodes history, OctopusImportPreviewAction unitAction, int? deploymentId, int? taskId, List<OctopusImportSourceMapRow> upserted, Action<Mock<IOctopusImportDeploymentHistoryImporter>> setupImporter, IOctopusImportHistoryDetailsStore store = null)
    {
        var ordered = new List<OctopusResourceNode> { history.Nodes.Project, history.Nodes.Environment, history.Channel, history.Release, history.Task, history.Deployment };
        var preview = Plan((history.Nodes.Project, OctopusImportPreviewAction.AlreadyImported, 1001), (history.Nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 3001), (history.Channel, OctopusImportPreviewAction.AlreadyImported, 2001), (history.Release, OctopusImportPreviewAction.AlreadyImported, 4001), (history.Task, unitAction, taskId), (history.Deployment, unitAction, deploymentId));
        var harness = CreateHarness(history.Nodes, BuildDependencyPlan(ordered), preview, true, false, false, sourceMap: SourceMap(history.Prior, upserted: upserted).Object, detailsStore: store);
        var machine = Node("Machines-1", OctopusResourceKind.Machine, OctopusDocumentKind.Machine, "web-01", new OctopusMachineDto { Id = "Machines-1", Name = "web-01" }, null);

        setupImporter(harness.DeploymentHistoryImporter);

        return harness with { Request = harness.Request with { Graph = harness.Request.Graph with { Resources = ordered.Append(machine).ToList() } } };
    }

    private static Action<Mock<IOctopusImportDeploymentHistoryImporter>> CaptureUnits(out List<OctopusImportHistoryUnit> units)
    {
        var captured = new List<OctopusImportHistoryUnit>();
        units = captured;

        return importer => importer.Setup(i => i.UpsertAsync(It.IsAny<OctopusImportHistoryUnit>(), It.IsAny<CancellationToken>())).Callback<OctopusImportHistoryUnit, CancellationToken>((unit, _) => captured.Add(unit)).ReturnsAsync((OctopusImportHistoryUnit unit, CancellationToken _) => new OctopusImportDeploymentHistoryResult(unit.ExistingDeploymentId ?? 5001, unit.ExistingTaskId ?? 6001, 8888));
    }

    private sealed record HistoryNodes(ScenarioNodes Nodes, OctopusResourceNode Channel, OctopusResourceNode Release, OctopusResourceNode Deployment, OctopusResourceNode Task, OctopusServerTaskDto TaskDto, OctopusImportPriorMappings Prior);

    private static HistoryNodes HistoryScenario(bool storeDeployment = true, bool storeTask = true, string storedTaskFingerprint = null, string storedDetailFingerprint = null)
    {
        var nodes = BuildMinimalNodes();
        var channel = Node("Channels-1", OctopusResourceKind.Channel, OctopusDocumentKind.Channel, "Default", new OctopusChannelDto { Id = "Channels-1", Name = "Default", ProjectId = "Projects-1", IsDefault = true }, nodes.Project.SourceId);
        var release = Node("Releases-1", OctopusResourceKind.Release, OctopusDocumentKind.Release, "1.0.0", new OctopusReleaseDto { Id = "Releases-1", ProjectId = "Projects-1", ChannelId = "Channels-1", Version = "1.0.0", ReleaseNotes = "New notes" }, null);
        var taskDto = new OctopusServerTaskDto { Id = "ServerTasks-1", Name = "Deploy", State = "Success", QueueTime = new DateTimeOffset(2024, 7, 10, 9, 47, 2, TimeSpan.Zero), CompletedTime = new DateTimeOffset(2024, 7, 10, 9, 48, 11, TimeSpan.Zero) };
        var task = Node("ServerTasks-1", OctopusResourceKind.ServerTask, OctopusDocumentKind.ServerTask, "Deploy", taskDto, null);
        var deployment = Node("Deployments-1", OctopusResourceKind.Deployment, OctopusDocumentKind.Deployment, "Deploy to TEST", new OctopusDeploymentDto { Id = "Deployments-1", Name = "Deploy to TEST", ProjectId = "Projects-1", ChannelId = "Channels-1", EnvironmentId = "Environments-1", ReleaseId = "Releases-1", TaskId = "ServerTasks-1", DeployedToMachineIds = ["Machines-1"] }, null);
        var stored = new List<(OctopusResourceNode, int, string)> { (nodes.Project, 1001, null), (channel, 2001, null), (nodes.Environment, 3001, null), (release, 4001, "old-notes") };

        if (storeDeployment) stored.Add((deployment, 5001, storedTaskFingerprint ?? OctopusImportFingerprint.ForTask(taskDto)));
        if (storeDeployment && storeTask) stored.Add((task, 6001, null));

        return new HistoryNodes(nodes, channel, release, deployment, task, taskDto, WithStoredDetails(Prior(stored.ToArray()), deployment, storedDetailFingerprint));
    }

    private static OctopusImportPriorMappings WithStoredDetails(OctopusImportPriorMappings prior, OctopusResourceNode deployment, string detailFingerprint)
    {
        if (detailFingerprint == null || !prior.TryGet(deployment.Kind.ToString(), deployment.SourceId, out var stored)) return prior;

        var mappings = prior.Mappings.ToDictionary(kv => kv.Key, kv => kv.Value);
        mappings[OctopusImportPriorMappings.Key(deployment.Kind.ToString(), deployment.SourceId)] = stored with { DetailFingerprint = detailFingerprint };

        return new OctopusImportPriorMappings(mappings, prior.InstallationId);
    }

    private static OctopusImportPriorMappings Prior(params (OctopusResourceNode Node, int DestinationId)[] rows)
        => Prior(rows.Select(r => (r.Node, r.DestinationId, (string)null)).ToArray());

    private static OctopusImportPriorMappings Prior(params (OctopusResourceNode Node, int DestinationId, string Fingerprint)[] rows)
        => new(rows.ToDictionary(r => OctopusImportPriorMappings.Key(r.Node.Kind.ToString(), r.Node.SourceId), r => new OctopusImportPriorMapping(r.DestinationId, "Created", r.Fingerprint, null)), InstallationId);

    private static Mock<IOctopusImportSourceMapDataProvider> SourceMap(OctopusImportPriorMappings prior, List<string> calls = null, List<OctopusImportSourceMapRow> upserted = null)
    {
        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();

        sourceMap.Setup(s => s.AcquireSpaceLockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Callback<int, CancellationToken>((space, _) => calls?.Add($"lock:{space}")).Returns(Task.CompletedTask);
        sourceMap.Setup(s => s.LoadAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Callback<int, CancellationToken>((space, _) => calls?.Add($"load:{space}")).ReturnsAsync(prior);
        sourceMap.Setup(s => s.UpsertAsync(It.IsAny<IReadOnlyList<OctopusImportSourceMapRow>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Callback<IReadOnlyList<OctopusImportSourceMapRow>, Guid, CancellationToken>((rows, _, _) => { calls?.Add("upsert"); upserted?.AddRange(rows); }).Returns(Task.CompletedTask);

        return sourceMap;
    }

    private static OctopusImportPreviewPlanDto Plan(params (OctopusResourceNode Node, OctopusImportPreviewAction Action, int? DestinationId)[] rows)
        => new()
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = rows.Select(r => new OctopusImportResourceResultDto { SourceId = r.Node.SourceId, SourceType = r.Node.Kind.ToString(), SourceName = r.Node.Name, PreviewAction = r.Action, OutcomeState = OctopusImportResourceOutcomeState.Pending, DestinationId = r.DestinationId }).ToList()
        };

    private static OctopusImportResourceResultDto Outcome(TestHarness harness, OctopusResourceNode node)
        => harness.SessionService.RecordedResult.Resources.Single(r => r.SourceId == node.SourceId);
}
