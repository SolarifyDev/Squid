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
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Contracts.Tentacle;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real <c>DeploymentPipelineRunner</c> with every real phase, lease keeper and completion handler; only the agent RPC
/// is faked). The user's cancel lands (row Cancelling, the run still holds the lease) just before the run ends with an error, the shape a pause write
/// refused by a Cancelling row takes in a manual-intervention step. The runner must end the task Cancelled, not resolve Cancelling as Failed.
/// </summary>
public class IntegrationCancelWinsOverRunError : DeploymentFixtureBase
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AnErrorWhileTheRowIsCancelling_EndsTheTaskCancelled_NotFailed()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
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
    }

    // Control: the same error without a cancel is still a failure.
    [Fact]
    public async Task TheSameErrorWithoutACancel_StillEndsTheTaskFailed()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new FailingAgent();

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();
            var run = BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None);

            await agent.Polled.WaitAsync(Bound).ConfigureAwait(false);
            agent.Fail();

            await Should.ThrowAsync<Exception>(() => run.WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => Register(b, agent)).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Failed);
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
