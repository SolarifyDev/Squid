using Autofac;
using Halibut;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real completion handler and runner, the real conditional state UPDATE; only the agent RPC is faked). The user's cancel
/// lands in the row (Cancelling) in the window between the completion handler reading the row as Executing and its own conditional write: the write matches
/// no row and is refused. The interleaving is forced by a hook on the handler's own read (it flips the row right after the read returns), never by sleeping.
/// The run must complete the user's cancel (Cancelled, one completion record, the cancel announced) instead of swallowing the refusal and leaving the row
/// Cancelling — holding its environment's slot with agent scripts running — until the stale-task reaper frees it.
/// </summary>
public class IntegrationTerminalWriteYieldsToCancel : DeploymentFixtureBase
{
    private const string Owner = "me:1:run";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public enum Outcome { Success, Failure, TimedOutPause, TransientPause, Suspended }

    [Theory]
    [InlineData(Outcome.Success)]
    [InlineData(Outcome.Failure)]
    [InlineData(Outcome.TimedOutPause)]
    [InlineData(Outcome.TransientPause)]
    [InlineData(Outcome.Suspended)]
    public async Task ACancelLandingBetweenTheReadAndTheWrite_CompletesTheCancel(Outcome outcome)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, TaskState.Executing).ConfigureAwait(false);

        var hook = new CancelAfterRead(() => SetStateAsync(seeded.TaskId, TaskState.Cancelling));

        // The success write has no read of its own (it always moves Executing -> Success), so the cancel is simply already in the row when it runs.
        if (outcome == Outcome.Success) await SetStateAsync(seeded.TaskId, TaskState.Cancelling).ConfigureAwait(false);
        else hook.Arm();

        await Run<IDeploymentCompletionHandler, IDeploymentDataProvider>(async (handler, deployments) =>
        {
            var deployment = await deployments.GetDeploymentByTaskIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);
            var ctx = new DeploymentTaskContext { ServerTaskId = seeded.TaskId, Deployment = deployment, LeaseOwner = Owner };

            await Should.NotThrowAsync(() => Drive(handler, outcome, ctx).WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => RegisterHook(b, hook)).ConfigureAwait(false);

        if (outcome != Outcome.Success) hook.Flipped.ShouldBeTrue("the hook must have forced the interleaving, or this test proves nothing");

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Cancelled);
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(1);
    }

    // Control: without the racing cancel each outcome is written as itself.
    [Theory]
    [InlineData(Outcome.Failure, TaskState.Failed)]
    [InlineData(Outcome.TimedOutPause, TaskState.Paused)]
    public async Task WithoutARacingCancel_TheOutcomeIsWrittenAsItself(Outcome outcome, string expected)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, TaskState.Executing).ConfigureAwait(false);

        await Run<IDeploymentCompletionHandler, IDeploymentDataProvider>(async (handler, deployments) =>
        {
            var deployment = await deployments.GetDeploymentByTaskIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);

            await Drive(handler, outcome, new DeploymentTaskContext { ServerTaskId = seeded.TaskId, Deployment = deployment, LeaseOwner = Owner }).WaitAsync(Bound).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(expected);
    }

    // Through the real runner's general catch (a non-transient error after the claim): the failure write is refused by the racing cancel. Before, the
    // refusal was swallowed and the row stayed Cancelling while DeploymentFailed was audited.
    [Fact]
    public async Task RunnerErrorPath_ACancelRacingTheFailureWrite_EndsCancelled_AnnouncedAsCancelledOnly()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var hook = new CancelAfterRead(() => SetStateAsync(seeded.TaskId, TaskState.Cancelling));

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();

            await Should.ThrowAsync<InvalidOperationException>(() => BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None).WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => RegisterRunner(b, hook)).ConfigureAwait(false);

        hook.Flipped.ShouldBeTrue("the hook must have forced the interleaving, or this test proves nothing");

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Cancelled);
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(1);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentCanceled).ConfigureAwait(false)).ShouldBe(1);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentFailed).ConfigureAwait(false)).ShouldBe(0);
    }

    // Through the real runner's in-try success branch: every phase succeeded, but the user cancelled (row Cancelling) before the outcome was written and the
    // cancel never reached this run's token. Before, DeploymentSucceeded was announced first and the cancel completed after it.
    [Fact]
    public async Task RunnerSuccessPath_WhenTheRowIsCancelling_EndsCancelled_AnnouncedAsCancelledOnly()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new CancelOnCompleteAgent(() => SetStateAsync(seeded.TaskId, TaskState.Cancelling));

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();

            await Should.NotThrowAsync(() => BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None).WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => b.RegisterInstance(new LocalClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance()).ConfigureAwait(false);

        agent.Completed.ShouldBeTrue("the script must have completed (and the cancel landed), or this test proves nothing");

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Cancelled);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentCanceled).ConfigureAwait(false)).ShouldBe(1);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentSucceeded).ConfigureAwait(false)).ShouldBe(0);
    }

    private static Task Drive(IDeploymentCompletionHandler handler, Outcome outcome, DeploymentTaskContext ctx) => outcome switch
    {
        Outcome.Success => handler.OnSuccessAsync(ctx, CancellationToken.None),
        Outcome.Failure => handler.OnFailureAsync(ctx, new InvalidOperationException("non-resumable timeout"), CancellationToken.None),
        Outcome.TimedOutPause => handler.OnTimedOutAsync(ctx, new InvalidOperationException("timeout"), CancellationToken.None),
        Outcome.TransientPause => handler.OnTransientPauseAsync(ctx, new InvalidOperationException("blip"), CancellationToken.None),
        Outcome.Suspended => handler.OnPausedAsync(ctx, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static void RegisterHook(ContainerBuilder builder, CancelAfterRead hook)
    {
        builder.RegisterInstance(hook).AsSelf().SingleInstance();
        builder.RegisterDecorator<HookedServerTaskService, IServerTaskService>();
    }

    private static void RegisterRunner(ContainerBuilder builder, CancelAfterRead hook)
    {
        RegisterHook(builder, hook);

        builder.RegisterInstance(new LocalClientFactory(new CancelOnCompleteAgent(() => Task.CompletedTask))).As<IHalibutClientFactory>().SingleInstance();
        builder.RegisterInstance(new ThrowingPhase(hook)).As<IDeploymentPipelinePhase>().SingleInstance();
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

    private Task SetRowAsync(int taskId, string state)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.State, state).SetProperty(t => t.LeaseOwner, Owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow), CancellationToken.None));

    private Task SetStateAsync(int taskId, string state)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.State, state), CancellationToken.None));

    private Task<string> LoadStateAsync(int taskId) => Run<IServerTaskDataProvider, string>(async p => (await p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None).ConfigureAwait(false)).State);

    private Task<int> CompletionCountAsync(int taskId)
        => Run<IRepository, int>(async repository =>
        {
            var deploymentIds = await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId).Select(d => d.Id).ToListAsync().ConfigureAwait(false);

            return await repository.QueryNoTracking<DeploymentCompletion>(c => deploymentIds.Contains(c.DeploymentId)).CountAsync().ConfigureAwait(false);
        });

    private Task<int> AuditCountAsync(int taskId, EventCategory category)
        => Run<SquidDbContext, int>(db => db.Set<Event>().AsNoTracking().CountAsync(e => e.ServerTaskId == taskId && e.Category == category));

    /// <summary>Flips the row to Cancelling right after the next read through <see cref="IServerTaskService.GetTaskAsync"/> returns, once, when armed.</summary>
    private sealed class CancelAfterRead(Func<Task> cancel)
    {
        private int _armed;

        public bool Flipped { get; private set; }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public async Task AfterReadAsync()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;

            await cancel().ConfigureAwait(false);

            Flipped = true;
        }
    }

    private sealed class HookedServerTaskService(IServerTaskService inner, CancelAfterRead hook) : IServerTaskService
    {
        public async Task<ServerTaskSummaryDto> GetTaskAsync(int taskId, CancellationToken ct = default)
        {
            var task = await inner.GetTaskAsync(taskId, ct).ConfigureAwait(false);

            await hook.AfterReadAsync().ConfigureAwait(false);

            return task;
        }

        public Task<StartExecutingResult> StartExecutingAsync(int taskId, CancellationToken ct = default) => inner.StartExecutingAsync(taskId, ct);

        public Task<StartExecutingResult> StartExecutingAsync(int taskId, string leaseOwner, CancellationToken ct = default) => inner.StartExecutingAsync(taskId, leaseOwner, ct);

        public Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, CancellationToken ct = default) => inner.TransitionStateAsync(taskId, expectedCurrentState, newState, ct);

        public Task TransitionStateAsync(int taskId, string expectedCurrentState, string newState, string leaseOwner, CancellationToken ct = default) => inner.TransitionStateAsync(taskId, expectedCurrentState, newState, leaseOwner, ct);

        public Task<ActivityLog> AddActivityNodeAsync(int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct = default) => inner.AddActivityNodeAsync(taskId, parentId, name, nodeType, status, sortOrder, ct);

        public Task UpdateActivityNodeStatusAsync(long nodeId, DeploymentActivityLogNodeStatus status, DateTimeOffset? endedAt = null, CancellationToken ct = default) => inner.UpdateActivityNodeStatusAsync(nodeId, status, endedAt, ct);

        public Task AddLogAsync(int taskId, long sequenceNumber, ServerTaskLogCategory category, string message, string source, long? activityNodeId = null, DateTimeOffset? occurredAt = null, string detail = null, CancellationToken ct = default) => inner.AddLogAsync(taskId, sequenceNumber, category, message, source, activityNodeId, occurredAt, detail, ct);

        public Task AddLogsAsync(int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken ct = default) => inner.AddLogsAsync(taskId, entries, ct);

        public Task<ServerTaskDetailsDto> GetTaskDetailsAsync(int taskId, bool? verbose = null, int? tail = null, CancellationToken ct = default) => inner.GetTaskDetailsAsync(taskId, verbose, tail, ct);

        public Task<ServerTaskLogPageDto> GetTaskLogsAsync(int taskId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default) => inner.GetTaskLogsAsync(taskId, afterSequenceNumber, take, ct);

        public Task<ServerTaskLogPageDto> GetTaskNodeLogsAsync(int taskId, long nodeId, long? afterSequenceNumber = null, int? take = null, CancellationToken ct = default) => inner.GetTaskNodeLogsAsync(taskId, nodeId, afterSequenceNumber, take, ct);

        public Task SetHasPendingInterruptionsAsync(int serverTaskId, bool hasPending, CancellationToken ct = default) => inner.SetHasPendingInterruptionsAsync(serverTaskId, hasPending, ct);

        public Task<bool> IsImportedAsync(int serverTaskId, CancellationToken ct = default) => inner.IsImportedAsync(serverTaskId, ct);

        public Task<(int TotalCount, List<ServerTaskSummaryDto> Items)> GetTaskListAsync(int projectId, string state = null, int pageIndex = 1, int pageSize = 30, CancellationToken ct = default) => inner.GetTaskListAsync(projectId, state, pageIndex, pageSize, ct);
    }

    /// <summary>Runs after every real phase (the task is claimed and leased), arms the hook, then fails with a non-transient error.</summary>
    private sealed class ThrowingPhase(CancelAfterRead hook) : IDeploymentPipelinePhase
    {
        public int Order => int.MaxValue;

        public Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
        {
            hook.Arm();

            throw new InvalidOperationException("a non-transient error after the claim");
        }
    }

    /// <summary>Completes the script successfully; on CompleteScript it first lands the user's cancel in the row (as a cancel served by another pod would).</summary>
    private sealed class CancelOnCompleteAgent(Func<Task> onComplete) : IAsyncScriptService
    {
        public bool Completed { get; private set; }

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => Task.FromResult(Resp(command.ScriptTicket));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => Task.FromResult(Resp(request.Ticket));

        public async Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
        {
            await onComplete().ConfigureAwait(false);

            Completed = true;

            return Resp(command.Ticket);
        }

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => Task.FromResult(Resp(command.Ticket));

        private static ScriptStatusResponse Resp(ScriptTicket ticket) => new(ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0);
    }

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
