using Autofac;
using Microsoft.EntityFrameworkCore;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Full-pipeline coverage (real <c>DeploymentPipelineRunner</c> + real <c>ExecuteStepsPhase</c> + real
/// <c>HalibutMachineExecutionStrategy</c> + real <c>HalibutScriptObserver</c> + real Postgres checkpoint;
/// only the agent RPC is faked) of the invariant: the agent is told to CancelScript if and only if the
/// task is heading to a terminal state. A PAUSE keeps the in-flight pointer so resume re-attaches to the
/// still-running script — cancelling that script would make resume attach to a dead one.
///
/// <para>The first test deliberately drives the whole pause → resume cycle against one blocking agent:
/// deployment timeout while the script runs → Paused, pointer kept, no CancelScript → resume re-attaches
/// to the SAME ticket (no duplicate StartScript) and the task succeeds.</para>
/// </summary>
public class IntegrationPausePreservesRemoteScript : DeploymentFixtureBase
{
    [Fact]
    public async Task ResumableTimeout_WhileScriptRunning_PausesWithoutCancelling_AndResumeReattachesToSameTicket()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        await RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(2), resumable: true).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused, "a resumable deployment timeout must PAUSE the task").ConfigureAwait(false);
        agent.CancelCalls.ShouldBe(0, "a paused task must leave its script running — CancelScript here makes resume attach to a cancelled script");
        agent.StartedTickets.Count.ShouldBe(1);
        (await InFlightTicketAsync(seeded).ConfigureAwait(false)).ShouldBe(agent.StartedTickets[0], "the in-flight pointer must survive the pause for resume");

        agent.Finish();
        await RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(30), resumable: true).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success, "resume must complete the re-attached script").ConfigureAwait(false);
        agent.StartedTickets.Count.ShouldBe(1, "resume must re-attach, not dispatch a duplicate StartScript");
        agent.ProbedTickets.ShouldAllBe(t => t == agent.StartedTickets[0], "every probe on resume must target the original ticket");
        agent.CancelCalls.ShouldBe(0);
    }

    [Fact]
    public async Task UnreachableAgent_WhileScriptRunning_PausesWithoutCancelling_PointerAndNoCancelAgree()
    {
        // Liveness probe declares the agent dead mid-script → transient pause. The pointer is preserved
        // for resume, so the observer must NOT also queue a CancelScript (a polling agent would receive
        // it on reconnect and kill the healthy script). The two facts must agree.
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();
        var deadProbeObserver = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 50, MaxPollIntervalMs = 100 }, new LivenessSettings { ProbeIntervalSeconds = 1, ProbeTimeoutSeconds = 1, FailureThreshold = 1 }, new DeadProbe());

        await RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(60), resumable: true, observer: deadProbeObserver).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused, "an unreachable agent is a transient pause").ConfigureAwait(false);
        agent.CancelCalls.ShouldBe(0, "pausing for an unreachable agent must not send CancelScript");
        (await InFlightTicketAsync(seeded).ConfigureAwait(false)).ShouldBe(agent.StartedTickets[0], "the pointer is kept for resume");
    }

    [Fact]
    public async Task RequiredStep_OneTargetFailsFast_PeerScriptIsCancelledOnItsAgent_AndTaskFails()
    {
        // A required step fans out; when target A's script fails, the phase cancels the peers through an
        // INTERNAL fail-fast token the runner does not own. The task then ends Failed and its checkpoint
        // (with the peer's in-flight pointer) is deleted, so the peer's still-running script must be
        // cancelled on its agent - otherwise it is orphaned. Guards against whitelisting only "known final"
        // reasons: an unlisted internal source must keep cancelling.
        var seeded = await SeedScriptDeploymentAsync(isRequired: true, machineCount: 2).ConfigureAwait(false);
        var agent = new FailOnePeerBlockOtherAgent();

        // The runner records the failure and rethrows it to the job host.
        await Should.ThrowAsync<Exception>(() => RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(60), resumable: true)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Failed, "one required target failing fails the task").ConfigureAwait(false);
        agent.StartedTickets.Count.ShouldBe(2, "both targets must have dispatched before the failure");
        agent.CancelledTickets.Count.ShouldBe(1, "exactly the still-running peer must be sent CancelScript; the failed target must not");
        agent.CancelledTickets[0].ShouldBe(agent.BlockerTicket, "CancelScript must target the peer's ticket");
    }

    [Fact]
    public async Task NonResumableTimeout_WhileScriptRunning_CancelsRemoteScript_AndFailsTask()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        await RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(2), resumable: false).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Failed, "a fail-fast timeout fails the task").ConfigureAwait(false);
        agent.CancelCalls.ShouldBe(1, "the task is terminal, its pointer is obsolete, so the still-running script must be cancelled");
    }

    [Fact]
    public async Task UserCancel_WhileScriptRunning_CancelsRemoteScript()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();

        // Signals the registry directly (what the cancel endpoint does after moving the task to Cancelling);
        // the task-state transition is not this test's concern — only whether the agent is told to cancel.
        await RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(60), resumable: true, afterAgentPolled: scope => scope.Resolve<ITaskCancellationRegistry>().TryCancel(seeded.TaskId)).ConfigureAwait(false);

        agent.CancelCalls.ShouldBeGreaterThanOrEqualTo(1, "user cancel is terminal — the agent script must be cancelled");
    }

    [Fact]
    public async Task HostShutdown_WhileScriptRunning_PausesKeepsSlotAndPointer_AndRedeliveryReattachesToSameTicket()
    {
        // Host shutdown / job-token cancellation (the user did NOT cancel). The task must end Paused - not
        // wedge in Executing - with its in-flight pointer kept and the agent script left running, the runner
        // must rethrow the cancellation (Hangfire re-queues an OperationCanceledException raised while its
        // shutdown token is cancelled), and the paused task must keep holding its environment slot. The
        // re-delivered job (a second ProcessAsync) then re-attaches to the SAME ticket and succeeds.
        const string tag = "env-slot-shutdown-test";
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetConcurrencyTagAsync(seeded.TaskId, tag).ConfigureAwait(false);
        var peerTaskId = await InsertPendingPeerTaskAsync(seeded.TaskId, tag).ConfigureAwait(false);
        var agent = new BlockingAgent();
        using var external = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(60), resumable: true, afterAgentPolled: _ => external.Cancel(), externalToken: external.Token)).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Paused, "a host shutdown must PAUSE the task, not leave it Executing or cancel it").ConfigureAwait(false);
        agent.CancelCalls.ShouldBe(0, "the script must keep running so the re-delivered job can re-attach");
        agent.StartedTickets.Count.ShouldBe(1);
        (await InFlightTicketAsync(seeded).ConfigureAwait(false)).ShouldBe(agent.StartedTickets[0], "the in-flight pointer must survive the pause");
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(0, "a paused deployment has not completed - no (failed) completion record");

        var peerAgent = new BlockingAgent();
        await RunPipelineAsync(peerTaskId, peerAgent, timeout: TimeSpan.FromSeconds(60), resumable: true).ConfigureAwait(false);

        await AssertStateAsync(peerTaskId, TaskState.Pending, "Paused holds the environment slot, so a peer task for the same environment must not claim it").ConfigureAwait(false);
        peerAgent.StartedTickets.Count.ShouldBe(0, "the blocked peer must not dispatch anything");

        agent.Finish();
        await RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(30), resumable: true).ConfigureAwait(false);

        await AssertStateAsync(seeded.TaskId, TaskState.Success, "the re-delivered job must resume from the checkpoint and complete").ConfigureAwait(false);
        agent.StartedTickets.Count.ShouldBe(1, "re-delivery must re-attach, not dispatch a duplicate StartScript");
        agent.ProbedTickets.ShouldAllBe(t => t == agent.StartedTickets[0], "every probe after re-delivery must target the original ticket");
        agent.CancelCalls.ShouldBe(0);
    }

    [Fact]
    public async Task HostShutdown_AndUserCancel_TogetherCancelsRemoteScript()
    {
        // User cancel wins over a simultaneous shutdown: the task heads to Cancelled, so the script is cancelled.
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();
        using var external = new CancellationTokenSource();

        await RunPipelineAsync(seeded.TaskId, agent, timeout: TimeSpan.FromSeconds(60), resumable: true, afterAgentPolled: scope =>
        {
            scope.Resolve<ITaskCancellationRegistry>().TryCancel(seeded.TaskId);
            external.Cancel();
        }, externalToken: external.Token).ConfigureAwait(false);

        agent.CancelCalls.ShouldBeGreaterThanOrEqualTo(1, "user cancel is terminal even when the host is also shutting down");
    }

    private Task SetConcurrencyTagAsync(int taskId, string tag)
        => Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var task = await repository.Query<ServerTask>(t => t.Id == taskId).FirstAsync().ConfigureAwait(false);
            task.ConcurrencyTag = tag;

            await repository.UpdateAsync(task).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        });

    // A bare Pending task sharing the tag: the slot check short-circuits before any phase runs, so no deployment graph is needed.
    private async Task<int> InsertPendingPeerTaskAsync(int sourceTaskId, string tag)
    {
        var peerId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var source = await repository.QueryNoTracking<ServerTask>(t => t.Id == sourceTaskId).FirstAsync().ConfigureAwait(false);

            var peer = new ServerTask
            {
                Name = "Slot Peer Task", Description = "Contends for the same environment slot", QueueTime = DateTimeOffset.UtcNow, State = TaskState.Pending,
                ServerTaskType = "Deploy", ProjectId = source.ProjectId, EnvironmentId = source.EnvironmentId, SpaceId = 1, LastModifiedDate = DateTimeOffset.UtcNow,
                BusinessProcessState = "Queued", StateOrder = 1, Weight = 1, BatchId = 0, JSON = string.Empty, HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(), DurationSeconds = 0, DataVersion = Array.Empty<byte>(), ConcurrencyTag = tag
            };

            await repository.InsertAsync(peer).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            peerId = peer.Id;
        }).ConfigureAwait(false);

        return peerId;
    }

    private Task<int> CompletionCountAsync(int taskId)
        => Run<IRepository, int>(async repository =>
        {
            var deploymentIds = await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId).Select(d => d.Id).ToListAsync().ConfigureAwait(false);

            return await repository.QueryNoTracking<DeploymentCompletion>(c => deploymentIds.Contains(c.DeploymentId)).CountAsync().ConfigureAwait(false);
        });

    private async Task RunPipelineAsync(int taskId, IAsyncScriptService agent, TimeSpan timeout, bool resumable, Action<ILifetimeScope> afterAgentPolled = null, CancellationToken externalToken = default, IHalibutScriptObserver observer = null)
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
                DeploymentTimeout = timeout,
                TimeoutResumable = resumable
            };

            var run = runner.ProcessAsync(taskId, externalToken);

            if (afterAgentPolled != null)
            {
                await ((BlockingAgent)agent).Polled.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                afterAgentPolled(scope);
            }

            await run.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }, extraRegistration: b =>
        {
            b.RegisterInstance(new FixedClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance();

            if (observer != null) b.RegisterInstance(observer).As<IHalibutScriptObserver>().SingleInstance();
        }).ConfigureAwait(false);
    }

    private Task AssertStateAsync(int taskId, string expected, string because)
        => Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(expected, because);
        });

    private Task<string> InFlightTicketAsync(SeededDeployment seeded)
        => Run<IDeploymentCheckpointService, string>(async checkpoints =>
        {
            var checkpoint = await checkpoints.LoadAsync(seeded.TaskId).ConfigureAwait(false);

            return checkpoint == null ? null : InFlightScriptMap.TryGet(checkpoint.InFlightScriptsJson, seeded.DispatchSlot);
        });

    // Script stays Running (and the observer keeps polling) until Finish() is called.
    internal sealed class BlockingAgent : IAsyncScriptService
    {
        private volatile bool _finished;
        private int _cancelCalls;

        public int CancelCalls => _cancelCalls;
        public List<string> StartedTickets { get; } = new();
        public List<string> ProbedTickets { get; } = new();
        public TaskCompletionSource PolledSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Polled => PolledSource.Task;

        public void Finish() => _finished = true;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            lock (StartedTickets) StartedTickets.Add(command.ScriptTicket.TaskId);

            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            lock (ProbedTickets) ProbedTickets.Add(request.Ticket.TaskId);
            PolledSource.TrySetResult();

            return Task.FromResult(Resp(request.Ticket, _finished ? ProcessState.Complete : ProcessState.Running, 0));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            Interlocked.Increment(ref _cancelCalls);

            return Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));
        }

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }

    // First ticket to start is the blocker (stays Running); the second fails (exit 1) but only once the
    // blocker has been polled, so the peer is provably in-flight when fail-fast cancels it.
    private sealed class FailOnePeerBlockOtherAgent : IAsyncScriptService
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _blockerPolled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> StartedTickets { get; } = new();
        public List<string> CancelledTickets { get; } = new();
        public string BlockerTicket { get; private set; }

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            lock (_gate)
            {
                StartedTickets.Add(command.ScriptTicket.TaskId);
                BlockerTicket ??= command.ScriptTicket.TaskId;
            }

            return Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));
        }

        public async Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            if (request.Ticket.TaskId == BlockerTicket)
            {
                _blockerPolled.TrySetResult();

                return Resp(request.Ticket, ProcessState.Running, 0);
            }

            await _blockerPolled.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            return Resp(request.Ticket, ProcessState.Complete, 1);
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 1));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            lock (_gate) CancelledTickets.Add(command.Ticket.TaskId);

            return Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));
        }

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
    }

    private sealed class DeadProbe : IAgentLivenessProbe
    {
        public Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }

    internal sealed class FixedClientFactory : IHalibutClientFactory
    {
        private readonly IAsyncScriptService _client;

        public FixedClientFactory(IAsyncScriptService client) => _client = client;

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _client;

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => new NoOpCapabilities();

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => throw new NotSupportedException();

        private sealed class NoOpCapabilities : IAsyncCapabilitiesService
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request) => Task.FromResult(new CapabilitiesResponse());
        }
    }
}
