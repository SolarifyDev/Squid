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
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums.Events;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real <c>DeploymentPipelineRunner</c> with every real phase, lease keeper and completion handler; only the agent RPC
/// is faked). The user's cancel lands in the database (row Cancelling, the run still holds the lease) but has not reached this run's token (no cancel watcher
/// here, as when the cross-pod signal is late), then a non-required step fails so the run ends through the in-try FailureEncountered branch. The task must end
/// Cancelled with its completion recorded once, not resolve Cancelling as Failed.
/// </summary>
public class IntegrationFailurePathHonoursCancel : DeploymentFixtureBase
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AFailedBatchWhileTheRowIsCancelling_EndsTheTaskCancelled_NotFailed()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: false).ConfigureAwait(false);
        var agent = new FailingAgent();

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();
            var run = BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None);

            await agent.Polled.WaitAsync(Bound).ConfigureAwait(false);

            await SetStateAsync(seeded.TaskId, TaskState.Cancelling).ConfigureAwait(false);
            agent.Fail();

            await Should.NotThrowAsync(() => run.WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => Register(b, agent)).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Cancelled);
        (await CompletionCountAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(1);

        // What is announced (audit history; the task log follows the same event) is the outcome that was written, not the one the run set out to write.
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentCanceled).ConfigureAwait(false)).ShouldBe(1);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentFailed).ConfigureAwait(false)).ShouldBe(0);
    }

    // Control: the same error without a cancel is still a failure.
    [Fact]
    public async Task TheSameFailedBatchWithoutACancel_StillEndsTheTaskFailed()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: false).ConfigureAwait(false);
        var agent = new FailingAgent();

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();
            var run = BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None);

            await agent.Polled.WaitAsync(Bound).ConfigureAwait(false);
            agent.Fail();

            await Should.NotThrowAsync(() => run.WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => Register(b, agent)).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Failed);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentFailed).ConfigureAwait(false)).ShouldBe(1);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentCanceled).ConfigureAwait(false)).ShouldBe(0);
    }

    private static void Register(ContainerBuilder builder, FailingAgent agent)
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

    private Task SetStateAsync(int taskId, string state)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.State, state), CancellationToken.None));

    private Task<int> CompletionCountAsync(int taskId)
        => Run<IRepository, int>(async repository =>
        {
            var deploymentIds = await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId).Select(d => d.Id).ToListAsync().ConfigureAwait(false);

            return await repository.QueryNoTracking<DeploymentCompletion>(c => deploymentIds.Contains(c.DeploymentId)).CountAsync().ConfigureAwait(false);
        });

    private Task<int> AuditCountAsync(int taskId, EventCategory category)
        => Run<SquidDbContext, int>(db => db.Set<Event>().AsNoTracking().CountAsync(e => e.ServerTaskId == taskId && e.Category == category));

    private Task<string> LoadStateAsync(int taskId) => Run<IServerTaskDataProvider, string>(async p => (await p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None).ConfigureAwait(false)).State);

    private sealed class FailingAgent : IAsyncScriptService
    {
        private volatile bool _failed;
        private readonly TaskCompletionSource _polled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Polled => _polled.Task;

        public void Fail() => _failed = true;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => Task.FromResult(Resp(command.ScriptTicket, ProcessState.Running, 0));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            _polled.TrySetResult();

            return Task.FromResult(Resp(request.Ticket, _failed ? ProcessState.Complete : ProcessState.Running, _failed ? 1 : 0));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 1));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete, 0));

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state, int exitCode) => new(ticket, state, exitCode, new List<ProcessOutput>(), 0);
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
