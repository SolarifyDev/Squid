using Autofac;
using Halibut;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;
using static Squid.IntegrationTests.Deployments.Pipeline.IntegrationPausePreservesRemoteScript;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH for the server side (real Postgres checkpoint with the in-flight ticket, the DI-resolved <c>ServerTaskControlService</c>, lease reaper,
/// <c>HalibutInFlightScriptCanceller</c>, selector and V1 / V2 dispatchers, and the real pipeline that produces the pause); only the agent RPC is faked.
/// A pause deliberately leaves the agent script running (its ticket stays on the checkpoint). Cancelling that paused task — by the user, or by the
/// lease reaper when the owner of a Cancelling task died — deletes the checkpoint, the only record of the ticket, so the script must be
/// cancelled on its agent first, in the dialect it was started with, and an unreachable agent must never block the cancel.
/// </summary>
public class IntegrationCancelPausedTaskCancelsAgentScript : DeploymentFixtureBase
{
    [Fact]
    public async Task UserCancelOfAPausedTask_CancelsTheStartedV1Script_ThenReleasesTheCheckpoint()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        await PauseAsync(seeded, agents).ConfigureAwait(false);
        agents.V1.CancelCalls.ShouldBe(0, "the pause itself must leave the script running");

        await Run<IServerTaskControlService>(s => s.CancelTaskAsync(seeded.TaskId), agents.Register).ConfigureAwait(false);

        agents.V1.CancelledTickets.ShouldBe(agents.V1.StartedTickets, "the script the pause left running must be told to cancel, by its own ticket");
        await AssertStateAsync(seeded.TaskId, TaskState.Cancelled).ConfigureAwait(false);
        (await CheckpointAsync(seeded).ConfigureAwait(false)).ShouldBeNull("the checkpoint is released after the cancel");
    }

    [Fact]
    public async Task UserCancelOfAPausedTask_CancelsAV2Script_OverV2Only()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { AdvertiseV2For = seeded.DispatchSlot.MachineId };
        await PauseAsync(seeded, agents).ConfigureAwait(false);
        agents.V2.StartedTickets.Count.ShouldBe(1, "the cache advertises V2, so the dispatch went over V2");

        await Run<IServerTaskControlService>(s => s.CancelTaskAsync(seeded.TaskId), agents.Register).ConfigureAwait(false);

        agents.V2.CancelledTickets.ShouldBe(agents.V2.StartedTickets);
        agents.V1.CancelCalls.ShouldBe(0, "a V2 script must be cancelled through V2, never V1");
        await AssertStateAsync(seeded.TaskId, TaskState.Cancelled).ConfigureAwait(false);
    }

    // A cache that no longer advertises V2 (rolled back agent, or a pod that never saw the capabilities) must not redirect the cancel to V1.
    [Fact]
    public async Task TheRecordedVersionWins_OverWhatTheCacheAdvertisesAtCancelTime()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents { AdvertiseV2For = seeded.DispatchSlot.MachineId };
        await PauseAsync(seeded, agents).ConfigureAwait(false);
        agents.AdvertiseV2For = -1;

        await Run<IServerTaskControlService>(s => s.CancelTaskAsync(seeded.TaskId), agents.Register).ConfigureAwait(false);

        agents.V2.CancelledTickets.ShouldBe(agents.V2.StartedTickets);
        agents.V1.CancelCalls.ShouldBe(0);
    }

    [Fact]
    public async Task UnreachableAgent_DoesNotBlockTheCancel_TaskStillEndsCancelled_WithinABound()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        await PauseAsync(seeded, agents).ConfigureAwait(false);
        agents.V1.Unreachable = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Run<IServerTaskControlService>(s => s.CancelTaskAsync(seeded.TaskId), agents.Register).ConfigureAwait(false);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the cancel budget is bounded; the agent being down must not hold the cancel");
        await AssertStateAsync(seeded.TaskId, TaskState.Cancelled).ConfigureAwait(false);
        (await CheckpointAsync(seeded).ConfigureAwait(false)).ShouldBeNull();
    }

    [Fact]
    public async Task ReaperCompletingACancellingTaskWhoseOwnerDied_CancelsTheAgentScript()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agents = new Agents();
        await PauseAsync(seeded, agents).ConfigureAwait(false);
        await MakeCancellingWithDeadOwnerAsync(seeded.TaskId).ConfigureAwait(false);

        var recovered = await Run<IServerTaskLeaseReaperService, int>(s => s.ReapStaleAsync(), agents.Register).ConfigureAwait(false);

        recovered.ShouldBeGreaterThanOrEqualTo(1);
        agents.V1.CancelledTickets.ShouldBe(agents.V1.StartedTickets, "the dead owner can no longer cancel its script; the reaper must, before deleting the only record of the ticket");
        await AssertStateAsync(seeded.TaskId, TaskState.Cancelled).ConfigureAwait(false);
        (await CheckpointAsync(seeded).ConfigureAwait(false)).ShouldBeNull();
    }

    // The pause (resumable timeout) leaves the script running and the ticket on the checkpoint.
    private async Task PauseAsync(SeededDeployment seeded, Agents agents)
    {
        await Run<ILifetimeScope>(async scope =>
        {
            var runner = new DeploymentPipelineRunner(
                scope.Resolve<IEnumerable<IDeploymentPipelinePhase>>(),
                scope.Resolve<IDeploymentLifecycle>(),
                scope.Resolve<IDeploymentCompletionHandler>(),
                scope.Resolve<ITaskCancellationRegistry>(),
                scope.Resolve<IServerTaskDataProvider>(),
                scope.Resolve<ISquidBackgroundJobClient>())
            {
                DeploymentTimeout = TimeSpan.FromSeconds(2),
                TimeoutResumable = true
            };

            await runner.ProcessAsync(seeded.TaskId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }, extraRegistration: agents.Register).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused).ConfigureAwait(false);
        (await CheckpointAsync(seeded).ConfigureAwait(false)).ShouldNotBeNull("the paused task must keep its in-flight ticket");
    }

    private Task MakeCancellingWithDeadOwnerAsync(int taskId)
        => Run<IRepository>(async repository =>
        {
            await repository.ExecuteUpdateAsync<ServerTask>(
                t => t.Id == taskId,
                s => s.SetProperty(t => t.State, TaskState.Cancelling).SetProperty(t => t.LeaseOwner, "dead-pod:1:x").SetProperty(t => t.LeaseRenewedAt, DateTimeOffset.UtcNow.AddHours(-1)),
                CancellationToken.None).ConfigureAwait(false);
        });

    private Task AssertStateAsync(int taskId, string expected)
        => Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expected);
        });

    private Task<string> CheckpointAsync(SeededDeployment seeded)
        => Run<IDeploymentCheckpointService, string>(async checkpoints =>
        {
            var checkpoint = await checkpoints.LoadAsync(seeded.TaskId).ConfigureAwait(false);

            return checkpoint?.InFlightScriptsJson;
        });

    private sealed class Agents : IHalibutClientFactory, IScriptServiceV2ClientFactory
    {
        public CancelRecordingV1Agent V1 { get; } = new();
        public CancelRecordingV2Agent V2 { get; } = new();
        public int AdvertiseV2For { get; set; } = -1;

        public void Register(ContainerBuilder b)
        {
            var cache = new InMemoryMachineRuntimeCapabilitiesCache();
            if (AdvertiseV2For > 0) cache.Store(AdvertiseV2For, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 });

            b.RegisterInstance(cache).As<IMachineRuntimeCapabilitiesCache>().SingleInstance();
            b.RegisterInstance(this).As<IHalibutClientFactory>().As<IScriptServiceV2ClientFactory>().SingleInstance();

            // Short cancel budget so the unreachable-agent case is bounded in seconds, not the 20s default.
            b.RegisterInstance(new HalibutSetting { RpcRetry = new RpcRetrySettings { CancelBudgetSeconds = 2 } }).AsSelf().SingleInstance();
        }

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => V1;

        public IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint) => V2;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();
    }

    // Script stays Running forever (the timeout pauses it); records every CancelScript.
    private sealed class CancelRecordingV1Agent : IAsyncScriptService
    {
        public List<string> StartedTickets { get; } = new();
        public List<string> CancelledTickets { get; } = new();
        public int CancelCalls => CancelledTickets.Count;
        public volatile bool Unreachable;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            lock (StartedTickets) StartedTickets.Add(command.ScriptTicket.TaskId);

            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => Task.FromResult(Resp(request.Ticket, ProcessState.Running));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            if (Unreachable) throw new HalibutClientException("agent unreachable");

            lock (CancelledTickets) CancelledTickets.Add(command.Ticket.TaskId);

            return Task.FromResult(Resp(command.Ticket, ProcessState.Complete));
        }

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
    }

    private sealed class CancelRecordingV2Agent : IAsyncScriptServiceV2
    {
        public List<string> StartedTickets { get; } = new();
        public List<string> CancelledTickets { get; } = new();

        public Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command)
        {
            lock (StartedTickets) if (!StartedTickets.Contains(command.ScriptTicket.TaskId)) StartedTickets.Add(command.ScriptTicket.TaskId);

            return Task.FromResult(Resp(command.ScriptTicket, ScriptStateV2.Running));
        }

        public Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request) => Task.FromResult(Resp(request.Ticket, ScriptStateV2.Running));

        public Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command)
        {
            lock (CancelledTickets) CancelledTickets.Add(command.Ticket.TaskId);

            return Task.FromResult(Resp(command.Ticket, ScriptStateV2.Completed));
        }

        public Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command)
            => Task.FromResult(new CompleteScriptResponseV2(command.Ticket, CompleteScriptOutcomeV2.Completed, Resp(command.Ticket, ScriptStateV2.Completed)));

        private static ScriptStatusResponseV2 Resp(ScriptTicket ticket, ScriptStateV2 state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
    }
}
