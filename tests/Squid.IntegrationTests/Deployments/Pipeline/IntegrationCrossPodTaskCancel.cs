using Autofac;
using Halibut;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using static Squid.IntegrationTests.Deployments.Pipeline.IntegrationPausePreservesRemoteScript;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH for the server side (real Postgres, the DI-resolved <c>IDeploymentTaskExecutor</c> with the real <c>ServerTaskCancelWatcher</c>, real
/// <c>ExecuteStepsPhase</c> / Halibut strategy / observer, real <c>ServerTaskControlService</c>); only the agent RPC is faked. Two "pods" share the
/// database but NOT an in-process registry: pod A runs the pipeline with the container's <c>TaskCancellationRegistry</c>; pod B serves the user's
/// cancel through a <c>ServerTaskControlService</c> wired to its OWN <c>TaskCancellationRegistry</c>, so its <c>TryCancel</c> reaches nothing on A.
/// Only the watcher can carry the cancel across.
/// </summary>
public class IntegrationCrossPodTaskCancel : DeploymentFixtureBase
{
    // Default watcher interval is 3s; allow several intervals of slack for a loaded CI box.
    private static readonly TimeSpan CancelBound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task CancelIssuedOnAnotherPod_CancelsTheAgentScript_AndTaskEndsCancelled()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        await RunPodAAsync(seeded.TaskId, agent, async podAScope =>
        {
            await agent.Polled.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            await CancelOnPodBAsync(podAScope, seeded.TaskId).ConfigureAwait(false);
        }).ConfigureAwait(false);

        agent.CancelCalls.ShouldBe(1, "a user cancel on another pod must still cancel the agent script (RemoteScriptCancelDecision: user cancel is terminal)");
        await AssertStateAsync(seeded.TaskId, TaskState.Cancelled).ConfigureAwait(false);
    }

    // Hangfire deletes the job when a cancel is served (ServerTaskControlService.DeleteJob) and aborts the running job's token on the pod that runs it;
    // that is the SAME external token a host shutdown fires. Fired here right after the cancel is served, ahead of the 3s watcher, it must be read as the
    // user cancel it is: agent script cancelled, task Cancelled — not "paused for shutdown" with the script left running and the row stuck Cancelling.
    [Fact]
    public async Task CancelIssuedOnAnotherPod_DeliveredAsHangfireJobAbort_IsStillACancel_NotAShutdown()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();
        using var jobToken = new CancellationTokenSource();

        await RunPodAAsync(seeded.TaskId, agent, async podAScope =>
        {
            await agent.Polled.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            await CancelOnPodBAsync(podAScope, seeded.TaskId).ConfigureAwait(false);

            jobToken.Cancel();
        }, jobToken.Token).ConfigureAwait(false);

        agent.CancelCalls.ShouldBe(1, "a job abort caused by a user cancel must still cancel the agent script");
        await AssertStateAsync(seeded.TaskId, TaskState.Cancelled).ConfigureAwait(false);
    }

    // The same external token with the row still Executing is a real host shutdown: pause (resumable), script untouched, job rethrown for re-delivery.
    [Fact]
    public async Task HostShutdown_WhileExecuting_StillPauses_AndLeavesTheAgentScriptRunning()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();
        using var jobToken = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => RunPodAAsync(seeded.TaskId, agent, async _ =>
        {
            await agent.Polled.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            jobToken.Cancel();
        }, jobToken.Token)).ConfigureAwait(false);

        agent.CancelCalls.ShouldBe(0, "a shutdown pause must leave the agent script running for the resume to re-attach");
        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);
    }

    [Fact]
    public async Task NoCancel_WatcherNeverTrips_AndTheDeploymentCompletes()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        await RunPodAAsync(seeded.TaskId, agent, async _ =>
        {
            await agent.Polled.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            // Longer than two default watcher intervals: had the watcher tripped spuriously the script would be cancelled by now.
            await Task.Delay(TimeSpan.FromSeconds(7)).ConfigureAwait(false);

            agent.Finish();
        }).ConfigureAwait(false);

        agent.CancelCalls.ShouldBe(0);
        await AssertStateAsync(seeded.TaskId, TaskState.Success).ConfigureAwait(false);
    }

    // Pod B: its own registry instance — its TryCancel hits an empty dictionary, exactly like a cancel landing on a pod that is not running the task.
    private static async Task CancelOnPodBAsync(ILifetimeScope podAScope, int taskId)
    {
        await using var podBScope = podAScope.BeginLifetimeScope();

        var podB = new ServerTaskControlService(
            podBScope.Resolve<IServerTaskDataProvider>(),
            podBScope.Resolve<IServerTaskService>(),
            new TaskCancellationRegistry(),
            podBScope.Resolve<IDeploymentInterruptionService>(),
            podBScope.Resolve<IDeploymentCheckpointService>(),
            podBScope.Resolve<ISquidBackgroundJobClient>());

        await podB.CancelTaskAsync(taskId).ConfigureAwait(false);
    }

    private async Task RunPodAAsync(int taskId, BlockingAgent agent, Func<ILifetimeScope, Task> whileRunning, CancellationToken jobToken = default)
    {
        await Run<ILifetimeScope>(async scope =>
        {
            var executor = scope.Resolve<IDeploymentTaskExecutor>();

            var run = executor.ProcessAsync(taskId, jobToken);

            await whileRunning(scope).ConfigureAwait(false);

            await run.WaitAsync(CancelBound).ConfigureAwait(false);
        }, extraRegistration: b => b.RegisterInstance(new FixedClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance()).ConfigureAwait(false);
    }

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expected);
        });
}
