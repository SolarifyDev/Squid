using Autofac;
using Halibut;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Contracts.Tentacle;
using static Squid.IntegrationTests.Deployments.Pipeline.IntegrationPausePreservesRemoteScript;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real <c>DeploymentPipelineRunner</c> with every real phase, real lease keeper, completion handler, task service and
/// registry; only the agent RPC is faked). A run that no longer owns its task must not touch what belongs to the run that does: a failed pause write
/// fences the run, the completion outcomes write their side effects only after the owned state write succeeded, and a takeover run in the same process
/// stays the task's cancel target.
/// </summary>
public class IntegrationLeaseOwnershipOnExit : DeploymentFixtureBase
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // The run's pause write is refused (the row is leased to someone else). Renewal was already stopped, so nothing proves ownership any more: the lease must
    // be marked lost, which is what stops the run, rather than leaving a run that continues until the reaper pauses the task and a second run resumes it.
    [Fact]
    public async Task APauseWriteThatFails_FencesTheRunsLease_AndLeavesTheRowToItsOwner()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, TaskState.Executing, "usurper:1:run").ConfigureAwait(false);

        await Run<IServerTaskLeaseKeeper, IServerTaskService>(async (keeper, service) =>
        {
            await using var lease = keeper.Start(seeded.TaskId, "old:1:run");
            var ctx = new DeploymentTaskContext { ServerTaskId = seeded.TaskId, Lease = lease };

            await Should.ThrowAsync<ServerTaskLeaseNotHeldException>(() => DeploymentSuspension.PauseAsync(service, seeded.TaskId, ctx, CancellationToken.None)).ConfigureAwait(false);

            lease.IsLost.ShouldBeTrue("a run whose pause write failed after renewal stopped must stop itself");
            lease.LostToken.IsCancellationRequested.ShouldBeTrue();
            ctx.Suspended.ShouldBeNull();
        }).ConfigureAwait(false);

        var task = await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false);
        task.State.ShouldBe(TaskState.Executing);
        task.LeaseOwner.ShouldBe("usurper:1:run");
    }

    // The run's lease was taken over without it noticing (a frozen VM): the owned state write refuses, and the completion record, which is not owner-guarded,
    // must not be written on the new owner's behalf. The control rows prove the same call does write both when the run still owns the task.
    [Theory]
    [InlineData(CompletionOutcome.Failure, false)]
    [InlineData(CompletionOutcome.Cancelled, false)]
    [InlineData(CompletionOutcome.Success, false)]
    [InlineData(CompletionOutcome.Failure, true)]
    [InlineData(CompletionOutcome.Cancelled, true)]
    [InlineData(CompletionOutcome.Success, true)]
    public async Task CompletionOutcome_WritesItsRecordOnlyWhenTheRunStillOwnsTheTask(CompletionOutcome outcome, bool ownsTheLease)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var startState = outcome == CompletionOutcome.Cancelled ? TaskState.Cancelling : TaskState.Executing;
        await SetRowAsync(seeded.TaskId, startState, ownsTheLease ? "me:1:run" : "usurper:1:run").ConfigureAwait(false);

        await DriveOutcomeAsync(seeded.TaskId, outcome, "me:1:run").ConfigureAwait(false);

        var task = await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false);
        task.State.ShouldBe(ownsTheLease ? TerminalStateOf(outcome) : startState);
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(ownsTheLease ? 1 : 0);
    }

    // A cancelled outcome from a run that lost the task still cancels the task's agent scripts (the task is Cancelling, so its owner cancels the same
    // scripts; doing it first means Cancelled never coexists with a live script), but it leaves the row, its outcome and its checkpoint to the owner.
    [Fact]
    public async Task ACancelledOutcomeFromARunThatLostTheTask_CancelsTheScriptsButLeavesTheRowToItsOwner()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();
            var run = BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None);

            await agent.Polled.WaitAsync(Bound).ConfigureAwait(false);

            await SetRowAsync(seeded.TaskId, TaskState.Cancelling, "usurper:1:run").ConfigureAwait(false);

            await ReportCancelledFromAsync(pod, seeded.TaskId, "stale:1:run").ConfigureAwait(false);

            agent.CancelCalls.ShouldBeGreaterThan(0, "the task is being cancelled: its scripts must be stopped whichever run reports it");

            var row = await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false);
            row.State.ShouldBe(TaskState.Cancelling);
            row.LeaseOwner.ShouldBe("usurper:1:run");

            await run.WaitAsync(Bound).ConfigureAwait(false);
        }, extraRegistration: b => RegisterFastLease(b, agent)).ConfigureAwait(false);
    }

    // A takeover run of the same task in the same process (the first run's lease went stale) is the task's cancel target: the user's cancel ends it, and the
    // first run, which lost its lease, neither displaced it nor unregistered it.
    [Fact]
    public async Task ATakeoverRunInTheSameProcess_IsTheCancelTarget_AndTheUserCancelCompletesIt()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        await Run<ILifetimeScope>(async scope =>
        {
            await using var podA = scope.BeginLifetimeScope();
            var runA = BuildRunner(podA).ProcessAsync(seeded.TaskId, CancellationToken.None);
            await agent.Polled.WaitAsync(Bound).ConfigureAwait(false);

            await MakeLeaseStaleAsync(seeded.TaskId, "dead:1:run").ConfigureAwait(false);

            await using var podB = scope.BeginLifetimeScope();
            var runB = BuildRunner(podB).ProcessAsync(seeded.TaskId, CancellationToken.None);
            await WaitForLeaseOwnerNotAsync(seeded.TaskId, "dead:1:run").ConfigureAwait(false);
            var probesAtTakeover = ProbeCount(agent);

            await runA.WaitAsync(Bound).ConfigureAwait(false);
            await WaitForAgentProbeAfterAsync(agent, probesAtTakeover).ConfigureAwait(false);

            await SetStateAsync(seeded.TaskId, TaskState.Cancelling).ConfigureAwait(false);
            podB.Resolve<ITaskCancellationRegistry>().TryCancel(seeded.TaskId).ShouldBeTrue("the takeover run must still be registered as the cancel target");

            await runB.WaitAsync(Bound).ConfigureAwait(false);
        }, extraRegistration: b => RegisterFastLease(b, agent)).ConfigureAwait(false);

        (await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(TaskState.Cancelled);
    }

    public enum CompletionOutcome { Success, Failure, Cancelled }

    private static string TerminalStateOf(CompletionOutcome outcome) => outcome switch
    {
        CompletionOutcome.Success => TaskState.Success,
        CompletionOutcome.Failure => TaskState.Failed,
        _ => TaskState.Cancelled
    };

    private Task DriveOutcomeAsync(int taskId, CompletionOutcome outcome, string runOwner)
        => Run<IDeploymentCompletionHandler, IDeploymentDataProvider>(async (handler, deployments) =>
        {
            var deployment = await deployments.GetDeploymentByTaskIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);
            var ctx = new DeploymentTaskContext { ServerTaskId = taskId, Deployment = deployment, LeaseOwner = runOwner };

            switch (outcome)
            {
                case CompletionOutcome.Success: await handler.OnSuccessAsync(ctx, CancellationToken.None).ConfigureAwait(false); break;
                case CompletionOutcome.Failure: await handler.OnFailureAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None).ConfigureAwait(false); break;
                default: await handler.OnCancelledAsync(ctx, CancellationToken.None).ConfigureAwait(false); break;
            }
        });

    // Resolved from the run's own scope, so the remote CancelScript reaches this test's agent (a fresh container would not).
    private static async Task ReportCancelledFromAsync(ILifetimeScope scope, int taskId, string runOwner)
    {
        var deployment = await scope.Resolve<IDeploymentDataProvider>().GetDeploymentByTaskIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);
        var ctx = new DeploymentTaskContext { ServerTaskId = taskId, Deployment = deployment, LeaseOwner = runOwner };

        await scope.Resolve<IDeploymentCompletionHandler>().OnCancelledAsync(ctx, CancellationToken.None).ConfigureAwait(false);
    }

    private static void RegisterFastLease(ContainerBuilder builder, BlockingAgent agent)
    {
        builder.RegisterInstance(new LocalClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance();
        builder.RegisterInstance(new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { RenewIntervalSeconds = 1, StaleSeconds = 30 } }).AsSelf().SingleInstance();
    }

    private static DeploymentPipelineRunner BuildRunner(ILifetimeScope scope)
        => new(
            scope.Resolve<IEnumerable<IDeploymentPipelinePhase>>(),
            scope.Resolve<IDeploymentLifecycle>(),
            scope.Resolve<IDeploymentCompletionHandler>(),
            scope.Resolve<ITaskCancellationRegistry>(),
            scope.Resolve<IServerTaskDataProvider>(),
            scope.Resolve<ISquidBackgroundJobClient>())
        {
            DeploymentTimeout = TimeSpan.FromSeconds(120),
            TimeoutResumable = true
        };

    private static int ProbeCount(BlockingAgent agent)
    {
        lock (agent.ProbedTickets) return agent.ProbedTickets.Count;
    }

    // The takeover run polls the agent only once it is past its claim and registered as the cancel target.
    private static async Task WaitForAgentProbeAfterAsync(BlockingAgent agent, int probesBefore)
    {
        var deadline = DateTime.UtcNow + Bound;

        while (ProbeCount(agent) <= probesBefore)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The takeover run never polled the agent");

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private async Task WaitForLeaseOwnerNotAsync(int taskId, string previousOwner)
    {
        var deadline = DateTime.UtcNow + Bound;

        while (DateTime.UtcNow < deadline)
        {
            var owner = (await LoadTaskAsync(taskId).ConfigureAwait(false)).LeaseOwner;

            if (owner != null && owner != previousOwner) return;

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"Task {taskId} was not taken over from {previousOwner} within {Bound}");
    }

    private Task<ServerTask> LoadTaskAsync(int taskId) => Run<IServerTaskDataProvider, ServerTask>(p => p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None));

    private Task SetRowAsync(int taskId, string state, string owner)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.State, state).SetProperty(t => t.LeaseOwner, owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow), CancellationToken.None));

    private Task SetStateAsync(int taskId, string state)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.State, state), CancellationToken.None));

    private Task MakeLeaseStaleAsync(int taskId, string owner)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.LeaseOwner, owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow.AddHours(-1)), CancellationToken.None));

    private Task<int> CompletionCountAsync(int taskId)
        => Run<IRepository, int>(async repository =>
        {
            var deploymentIds = await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId).Select(d => d.Id).ToListAsync().ConfigureAwait(false);

            return await repository.QueryNoTracking<DeploymentCompletion>(c => deploymentIds.Contains(c.DeploymentId)).CountAsync().ConfigureAwait(false);
        });

    private sealed class LocalClientFactory(IAsyncScriptService client) : IHalibutClientFactory
    {
        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => client;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilities();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        private sealed class NoOpCapabilities : IAsyncCapabilitiesService
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request) => Task.FromResult(new CapabilitiesResponse());
        }
    }
}
