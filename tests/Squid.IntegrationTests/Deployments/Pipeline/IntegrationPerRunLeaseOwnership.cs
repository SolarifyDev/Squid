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
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using static Squid.IntegrationTests.Deployments.Pipeline.IntegrationPausePreservesRemoteScript;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real <c>DeploymentPipelineRunner</c> with every real phase, real <c>ExecuteStepsPhase</c> / Halibut strategy / observer,
/// real lease keeper and interruption service; only the agent RPC is faked). Covers what a run's lease means: ownership belongs to one RUN, never to the
/// process, and a run that loses it stops.
/// </summary>
public class IntegrationPerRunLeaseOwnership : DeploymentFixtureBase
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // The loser of a lease: its renewal updates nothing because someone else now holds the row. The pipeline must stop dispatching, leave the agent script
    // running and write nothing — the task state is whatever the new owner (or the reaper) decided.
    [Fact]
    public async Task LeaseTakenOverMidRun_StopsThePipeline_WritesNothing_AndLeavesTheScriptRunning()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();
            var run = BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None);

            await agent.Polled.WaitAsync(Bound).ConfigureAwait(false);

            await TakeLeaseAsync(seeded.TaskId, "usurper:1:run").ConfigureAwait(false);

            await run.WaitAsync(Bound).ConfigureAwait(false);
        }, extraRegistration: b => RegisterFastLease(b, agent)).ConfigureAwait(false);

        var task = await LoadTaskAsync(seeded.TaskId).ConfigureAwait(false);
        task.State.ShouldBe(TaskState.Executing, "the loser must not change the state the new owner holds");
        task.LeaseOwner.ShouldBe("usurper:1:run");
        agent.CancelCalls.ShouldBe(0, "the script stays running for whoever owns the task now");
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(0);
    }

    private static void RegisterFastLease(ContainerBuilder builder, BlockingAgent agent)
    {
        builder.RegisterInstance(new LocalClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance();
        builder.RegisterInstance(new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { RenewIntervalSeconds = 5, StaleSeconds = 30 } }).AsSelf().SingleInstance();
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

    private async Task WaitForStateAsync(int taskId, string state)
    {
        var deadline = DateTime.UtcNow + Bound;

        while (DateTime.UtcNow < deadline)
        {
            if ((await LoadTaskAsync(taskId).ConfigureAwait(false)).State == state) return;

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"Task {taskId} did not reach {state} within {Bound}");
    }

    private Task<ServerTask> LoadTaskAsync(int taskId) => Run<IServerTaskDataProvider, ServerTask>(p => p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None));

    private Task TakeLeaseAsync(int taskId, string owner)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.LeaseOwner, owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow), CancellationToken.None));

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider => (await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false)).State.ShouldBe(expected));

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
