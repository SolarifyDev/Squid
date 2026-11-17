using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.LifeCycle;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>Confirmation over data shapes real Octopus servers produce (random GUID ids, ids out of list order).</summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    // Octopus phase ids are random GUIDs: their lexical order says nothing about the phase order. Each created Squid phase must be
    // recorded against the Octopus phase it was created from (same list position), never against the phase whose id sorts there.
    [Fact]
    public async Task CreateLifecycle_PhaseIdsOutOfLexicalOrder_EachPhaseRecordedAgainstTheSquidPhaseMadeFromIt()
    {
        var nodes = BuildMinimalNodes();
        string[] phaseIds = ["f0000000-0000-4000-8000-000000000001", "a0000000-0000-4000-8000-000000000002", "c0000000-0000-4000-8000-000000000003"];
        string[] phaseNames = ["Dev", "Test", "Prod"];
        var phaseDtos = phaseIds.Zip(phaseNames, (id, name) => new OctopusLifecyclePhaseDto { Id = id, Name = name }).ToList();
        var lifecycle = Node("Lifecycles-9", OctopusResourceKind.Lifecycle, OctopusDocumentKind.Lifecycle, "Three phases", new OctopusLifecycleDto { Id = "Lifecycles-9", Name = "Three phases", Slug = "three-phases", Phases = phaseDtos }, null);
        var phases = phaseDtos.Select((p, i) => Node(OctopusResourceGraphBuilder.BuildChildSourceId(lifecycle.SourceId, "phase", p.Id, i), OctopusResourceKind.LifecyclePhase, OctopusDocumentKind.Lifecycle, p.Name, p, lifecycle.SourceId)).ToList();
        var ordered = new List<OctopusResourceNode> { lifecycle };
        ordered.AddRange(phases.OrderBy(p => p.SourceId, StringComparer.OrdinalIgnoreCase));
        var harness = CreateHarness(nodes, BuildDependencyPlan(ordered), Plan(ordered.Select(n => (n, OctopusImportPreviewAction.Create, (int?)null)).ToArray()), true, false, false);
        harness.Mediator.Setup(m => m.SendAsync<CreateLifeCycleCommand, CreateLifeCycleResponse>(It.IsAny<CreateLifeCycleCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CreateLifeCycleResponse { Data = new CreateLifeCycleResponseData { LifecyclePhase = new LifecycleDetailDto { Lifecycle = new LifeCycleDto { Id = 90, Name = "Three phases" }, Phases = phaseNames.Select((name, i) => new LifecyclePhaseDto { Id = 91 + i, LifecycleId = 90, Name = name, SortOrder = i + 1 }).ToList() } } });

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var recorded = harness.SessionService.RecordedResult.IdMappings.Where(m => m.SourceType == nameof(OctopusResourceKind.LifecyclePhase)).ToDictionary(m => m.SourceName, m => m.DestinationId);
        recorded.ShouldBe(new Dictionary<string, int> { ["Dev"] = 91, ["Test"] = 92, ["Prod"] = 93 }, ignoreOrder: true);
    }

    // A project with thousands of Octopus releases: only its first release builds the variable and process snapshots; every later one
    // in the same confirmation is created against those (the project's configuration is final by then: it ranks before history).
    [Fact]
    public async Task CreateReleases_OfOneProject_OnlyTheFirstTakesSnapshots_TheRestReuseThem()
    {
        var nodes = BuildRichNodes();
        var source = nodes.Release.GetSource<OctopusReleaseDto>();
        var releases = Enumerable.Range(2, 3).Select(i => Node($"Releases-{i}", OctopusResourceKind.Release, OctopusDocumentKind.Release, $"1.0.{i}", new OctopusReleaseDto { Id = $"Releases-{i}", Name = $"1.0.{i}", ProjectId = source.ProjectId, ChannelId = source.ChannelId, Version = $"1.0.{i}" }, null)).ToList();
        var ordered = nodes.RichOrderedResources.Concat(releases).ToList();
        var harness = CreateHarness(nodes, BuildDependencyPlan(ordered), BuildPreviewPlan(ordered, reusedResources: [nodes.Environment]), true, false, false);
        var nextId = 2000;
        harness.Mediator.Setup(m => m.SendAsync<CreateReleaseCommand, CreateReleaseResponse>(It.IsAny<CreateReleaseCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new CreateReleaseResponse { Data = new ReleaseDto { Id = ++nextId, ProjectVariableSetSnapshotId = 501, ProjectDeploymentProcessSnapshotId = 502 } });

        var result = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var commands = harness.Mediator.Invocations.Select(i => i.Arguments.FirstOrDefault()).OfType<CreateReleaseCommand>().ToList();
        commands.Count.ShouldBe(4);
        commands[0].HistoricalSnapshots.ShouldBeNull("the first release of the project takes its snapshots");
        commands.Skip(1).ShouldAllBe(c => c.HistoricalSnapshots == new HistoricalReleaseSnapshots(501, 502));
    }
}
