using Squid.Core.Persistence.Db;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Events;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Enums.Events;
using Squid.Message.Requests.Events;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// Full-pipeline E2E for the deployment-history audit stream. Drives the REAL
/// <c>DeploymentPipelineRunner</c> end-to-end (real DI graph, real lifecycle publisher,
/// real Postgres, real DbUp-migrated event table) through the in-process
/// <c>CapturingExecutionStrategy</c> — so NO Kind cluster is needed and the test is
/// deterministic. It proves the audit handler fires from inside <c>ProcessAsync</c> and
/// the persisted Event feed reflects the lifecycle in newest-first order. Deliberately
/// NOT in the "KindCluster" collection (no kubectl), so it cannot flake on cluster infra.
/// </summary>
[Trait("Category", "E2E")]
public class DeploymentHistoryE2ETests : IClassFixture<DeploymentPipelineFixture<DeploymentHistoryE2ETests>>
{
    private const int SeededSpaceId = 1;   // K8sTestDataSeeder seeds everything in space 1

    private readonly DeploymentPipelineFixture<DeploymentHistoryE2ETests> _fixture;

    public DeploymentHistoryE2ETests(DeploymentPipelineFixture<DeploymentHistoryE2ETests> fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SuccessfulPipeline_RecordsStartedThenSucceeded_InTheEventFeed()
    {
        _fixture.ExecutionCapture.Clear();

        var serverTaskId = await SeedAsync();

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTaskId, CancellationToken.None)).ConfigureAwait(false);

        await AssertTaskStateAsync(TaskState.Success);

        await _fixture.Run<IEventService>(async events =>
        {
            var feed = await events.GetEventsAsync(new GetEventsRequest { SpaceId = SeededSpaceId });
            var categories = feed.Events.Select(e => e.Category).ToList();

            categories.Contains((int)EventCategory.DeploymentStarted).ShouldBeTrue("the pipeline must record that the deployment started");
            categories.Contains((int)EventCategory.DeploymentSucceeded).ShouldBeTrue("and that it succeeded");
            feed.Events[0].Category.ShouldBe((int)EventCategory.DeploymentSucceeded, "the terminal success is the newest event");

            var succeeded = feed.Events.First(e => e.Category == (int)EventCategory.DeploymentSucceeded);
            succeeded.ReleaseId.ShouldNotBeNull("the audit event links back to its release");
            succeeded.DeploymentId.ShouldNotBeNull();
            // references carry the seeded release version (1.0.0) for the history UI to render
            succeeded.ReferencesJson.ShouldContain("1.0.0");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FailedPipeline_RecordsDeploymentFailed_InTheEventFeed()
    {
        _fixture.ExecutionCapture.Clear();
        _fixture.ExecutionCapture.ResultFactory = _ => new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = { "simulated step failure" } };

        var serverTaskId = await SeedAsync();

        // A hard step failure rethrows out of ProcessAsync — but only AFTER the runner has
        // emitted DeploymentFailedEvent and recorded the terminal Failed state. The audit
        // assertion below is the point: the failure is durably recorded despite the rethrow.
        try
        {
            await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTaskId, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (DeploymentScriptException)
        {
        }

        await AssertTaskStateAsync(TaskState.Failed);

        await _fixture.Run<IEventService>(async events =>
        {
            var feed = await events.GetEventsAsync(new GetEventsRequest { SpaceId = SeededSpaceId });

            feed.Events.Select(e => e.Category).Contains((int)EventCategory.DeploymentFailed).ShouldBeTrue("a failed step must surface a DeploymentFailed audit event");
        }).ConfigureAwait(false);
    }

    private async Task<int> SeedAsync()
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork);
            await seeder.SeedAsync(createFeedSecrets: false).ConfigureAwait(false);
            serverTaskId = seeder.ServerTaskId;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task AssertTaskStateAsync(string expected)
    {
        await _fixture.Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);

            tasks.ShouldContain(t => t.State == expected, $"the seeded deployment should reach {expected}");
        }).ConfigureAwait(false);
    }
}
