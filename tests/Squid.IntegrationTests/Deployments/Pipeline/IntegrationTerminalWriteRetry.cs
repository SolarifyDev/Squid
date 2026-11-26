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
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Contracts.Tentacle;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real completion handler / runner / data providers; only the agent RPC is faked). A real transient database error
/// (a trigger on deployment_completion raising SQLSTATE 40001, serialization_failure, for the first N inserts) hits the transaction that writes the terminal
/// state and its completion row. The write is retried: a finished deployment ends Success with ONE completion row instead of the success being rolled back and
/// the runner's failure path recording Failed. When the fault never clears, the failure surfaces as it always did.
/// </summary>
public class IntegrationTerminalWriteRetry : DeploymentFixtureBase
{
    private const string Owner = "me:1:run";
    private const string Sequence = "test_completion_fault_seq";
    private const string Function = "test_completion_fault_fn";
    private const string Trigger = "test_completion_fault_trg";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task HandlerSuccess_WhenTheFirstTwoTransactionsFailTransiently_EndsSuccessWithOneCompletion()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, TaskState.Executing).ConfigureAwait(false);

        await WithInjectedFaultsAsync(2, async () =>
        {
            await DriveSuccessAsync(seeded.TaskId).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Success);
        (await CompletionStatesAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe([TaskState.Success]);
    }

    // The fault never clears: the bound is honoured and the failure surfaces as today (the row is untouched, nothing recorded).
    [Fact]
    public async Task HandlerSuccess_WhenEveryTransactionFailsTransiently_GivesUpAndThrows_WritingNothing()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, TaskState.Executing).ConfigureAwait(false);

        await WithInjectedFaultsAsync(1000, async () =>
        {
            await Should.ThrowAsync<Exception>(() => DriveSuccessAsync(seeded.TaskId)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Executing);
        (await CompletionStatesAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBeEmpty();
    }

    // Seam: the configured bound reaches the handler the container builds (a bound of one means the first transient fault is final).
    [Fact]
    public async Task HandlerSuccess_HonoursTheConfiguredAttemptBound()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, TaskState.Executing).ConfigureAwait(false);

        await WithInjectedFaultsAsync(1, async () =>
        {
            await Run<IDeploymentCompletionHandler, IDeploymentDataProvider>(async (handler, deployments) =>
            {
                var deployment = await deployments.GetDeploymentByTaskIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);

                await Should.ThrowAsync<Exception>(() => handler.OnSuccessAsync(new DeploymentTaskContext { ServerTaskId = seeded.TaskId, Deployment = deployment, LeaseOwner = Owner }, CancellationToken.None)).ConfigureAwait(false);
            }, extraRegistration: b => b.RegisterInstance(new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = 1 } }).AsSelf().SingleInstance()).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Executing);
    }

    // Through the real runner: without the retry, the rolled-back success reaches the runner's catch and the deployment is written Failed.
    [Fact]
    public async Task RunnerSuccess_WhenTheTerminalTransactionFailsOnceTransiently_StillEndsSuccess_NotFailed()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);

        await WithInjectedFaultsAsync(1, async () =>
        {
            await Run<ILifetimeScope>(async scope =>
            {
                await using var pod = scope.BeginLifetimeScope();

                await Should.NotThrowAsync(() => BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None).WaitAsync(Bound)).ConfigureAwait(false);
            }, extraRegistration: Register).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Success);
        (await CompletionStatesAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe([TaskState.Success]);
    }

    private static void Register(ContainerBuilder builder)
    {
        builder.RegisterInstance(new LocalClientFactory(new SucceedingAgent())).As<IHalibutClientFactory>().SingleInstance();
        builder.RegisterInstance(new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { RenewIntervalSeconds = 1, StaleSeconds = 30 }, TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = 3, RetryDelayMilliseconds = 10 } }).AsSelf().SingleInstance();
    }

    private Task DriveSuccessAsync(int taskId)
        => Run<IDeploymentCompletionHandler, IDeploymentDataProvider>(async (handler, deployments) =>
        {
            var deployment = await deployments.GetDeploymentByTaskIdAsync(taskId, CancellationToken.None).ConfigureAwait(false);

            await handler.OnSuccessAsync(new DeploymentTaskContext { ServerTaskId = taskId, Deployment = deployment, LeaseOwner = Owner }, CancellationToken.None).ConfigureAwait(false);
        });

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

    // A sequence is not rolled back with the transaction, so exactly the first N insert attempts fail whatever the retries do.
    private async Task WithInjectedFaultsAsync(int failures, Func<Task> body)
    {
        await Run<IRepository>(r => r.Database.ExecuteSqlRawAsync($"CREATE SEQUENCE {Sequence}; CREATE FUNCTION {Function}() RETURNS trigger AS $$ BEGIN IF nextval('{Sequence}') <= {failures} THEN RAISE EXCEPTION 'injected serialization failure' USING ERRCODE = '40001'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql; CREATE TRIGGER {Trigger} BEFORE INSERT ON deployment_completion FOR EACH ROW EXECUTE FUNCTION {Function}();")).ConfigureAwait(false);

        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            await Run<IRepository>(r => r.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS {Trigger} ON deployment_completion; DROP FUNCTION IF EXISTS {Function}(); DROP SEQUENCE IF EXISTS {Sequence};")).ConfigureAwait(false);
        }
    }

    private Task SetRowAsync(int taskId, string state)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.State, state).SetProperty(t => t.LeaseOwner, Owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow), CancellationToken.None));

    private Task<string> LoadStateAsync(int taskId) => Run<IServerTaskDataProvider, string>(async p => (await p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None).ConfigureAwait(false)).State);

    private Task<List<string>> CompletionStatesAsync(int taskId)
        => Run<IRepository, List<string>>(async repository =>
        {
            var deploymentIds = await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId).Select(d => d.Id).ToListAsync().ConfigureAwait(false);

            return await repository.QueryNoTracking<DeploymentCompletion>(c => deploymentIds.Contains(c.DeploymentId)).Select(c => c.State).ToListAsync().ConfigureAwait(false);
        });

    private sealed class SucceedingAgent : IAsyncScriptService
    {
        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => Task.FromResult(Resp(command.ScriptTicket, ProcessState.Complete));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => Task.FromResult(Resp(request.Ticket, ProcessState.Complete));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => Task.FromResult(Resp(command.Ticket, ProcessState.Complete));

        private static ScriptStatusResponse Resp(ScriptTicket ticket, ProcessState state) => new(ticket, state, 0, new List<ProcessOutput>(), 0);
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
