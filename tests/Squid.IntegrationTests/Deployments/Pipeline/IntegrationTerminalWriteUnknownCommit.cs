using System.Data.Common;
using Halibut;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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
using Squid.Core.Services.Events;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums.Events;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real completion handler / runner / transaction / conditional state UPDATE; only the agent RPC is faked). The commit of
/// the transaction that writes the outcome REACHES the server, then the acknowledgement is lost: an EF transaction interceptor throws a transient Npgsql
/// connection error right after the real commit (as a connection dropped mid-acknowledgement does). The retry's conditional write matches no row — the row is
/// already in the target state with this run's completion record. That must be accepted as the earlier attempt's write, exactly once: before, the refusal
/// reached the runner's failure path, a deployment that succeeded was announced Failed after Succeeded, its checkpoint was kept and auto-deploys never ran.
/// </summary>
public class IntegrationTerminalWriteUnknownCommit : DeploymentFixtureBase
{
    private const string Owner = "me:1:run";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public enum Outcome { Success, Failure, Cancel, Pause }

    [Theory]
    [InlineData(Outcome.Success, TaskState.Executing, TaskState.Success, TaskState.Success)]
    [InlineData(Outcome.Failure, TaskState.Executing, TaskState.Failed, TaskState.Failed)]
    [InlineData(Outcome.Cancel, TaskState.Cancelling, TaskState.Cancelled, TaskState.Failed)]
    [InlineData(Outcome.Pause, TaskState.Executing, TaskState.Paused, null)]
    public async Task AnOutcomeWhoseCommitLandedButWasNotAcknowledged_IsAcceptedOnce(Outcome outcome, string from, string target, string completionState)
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await SetRowAsync(seeded.TaskId, from).ConfigureAwait(false);
        var lostAck = new LostCommitAcknowledgement(target, await ConnectionStringAsync().ConfigureAwait(false));

        await Run<IDeploymentCompletionHandler, IDeploymentDataProvider>(async (handler, deployments) =>
        {
            var deployment = await deployments.GetDeploymentByTaskIdAsync(seeded.TaskId, CancellationToken.None).ConfigureAwait(false);

            await Should.NotThrowAsync(() => Drive(handler, outcome, new DeploymentTaskContext { ServerTaskId = seeded.TaskId, Deployment = deployment, LeaseOwner = Owner }).WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => RegisterLostAck(b, lostAck)).ConfigureAwait(false);

        lostAck.Fired.ShouldBeTrue("the acknowledgement of the committed outcome must have been lost, or this test proves nothing");

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(target);
        (await CompletionStatesAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(completionState == null ? [] : [completionState]);
    }

    // Through the real runner: the deployment that succeeded is recorded and announced Success only — never Failed after Succeeded.
    [Fact]
    public async Task RunnerSuccess_WhoseCommitAcknowledgementIsLost_EndsSuccess_AnnouncedOnce()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var lostAck = new LostCommitAcknowledgement(TaskState.Success, await ConnectionStringAsync().ConfigureAwait(false));

        await Run<ILifetimeScope>(async scope =>
        {
            await using var pod = scope.BeginLifetimeScope();

            await Should.NotThrowAsync(() => BuildRunner(pod).ProcessAsync(seeded.TaskId, CancellationToken.None).WaitAsync(Bound)).ConfigureAwait(false);
        }, extraRegistration: b => RegisterRunner(b, lostAck)).ConfigureAwait(false);

        lostAck.Fired.ShouldBeTrue("the acknowledgement of the committed success must have been lost, or this test proves nothing");

        (await LoadStateAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe(TaskState.Success);
        (await CompletionStatesAsync(seeded.TaskId).ConfigureAwait(false)).ShouldBe([TaskState.Success]);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentSucceeded).ConfigureAwait(false)).ShouldBe(1);
        (await AuditCountAsync(seeded.TaskId, EventCategory.DeploymentFailed).ConfigureAwait(false)).ShouldBe(0);
    }

    private static Task Drive(IDeploymentCompletionHandler handler, Outcome outcome, DeploymentTaskContext ctx) => outcome switch
    {
        Outcome.Success => handler.OnSuccessAsync(ctx, CancellationToken.None),
        Outcome.Failure => handler.OnFailureAsync(ctx, new InvalidOperationException("a step failed"), CancellationToken.None),
        Outcome.Cancel => handler.OnCancelledAsync(ctx, CancellationToken.None),
        Outcome.Pause => handler.OnTimedOutAsync(ctx, new InvalidOperationException("timeout"), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    // The scope's DbContext is the real one on the same isolated database, with the interceptors added: every write the scope makes commits through them.
    private static void RegisterLostAck(ContainerBuilder builder, LostCommitAcknowledgement lostAck)
    {
        builder.RegisterInstance(new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { RenewIntervalSeconds = 1, StaleSeconds = 30 }, TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = 3, RetryDelayMilliseconds = 10 } }).AsSelf().SingleInstance();
        builder.Register(c => new SquidDbContext(lostAck.Options(), new InternalUser(), c.Resolve<IAuditDocumentRegistry>())).AsSelf().As<DbContext>().AsImplementedInterfaces().InstancePerLifetimeScope();
    }

    private Task<string> ConnectionStringAsync() => Run<SquidDbContext, string>(db => Task.FromResult(db.Database.GetConnectionString()));

    private static void RegisterRunner(ContainerBuilder builder, LostCommitAcknowledgement lostAck)
    {
        RegisterLostAck(builder, lostAck);

        builder.RegisterInstance(new LocalClientFactory(new SucceedingAgent())).As<IHalibutClientFactory>().SingleInstance();
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

    private Task<string> LoadStateAsync(int taskId) => Run<IServerTaskDataProvider, string>(async p => (await p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None).ConfigureAwait(false)).State);

    private Task<List<string>> CompletionStatesAsync(int taskId)
        => Run<IRepository, List<string>>(async repository =>
        {
            var deploymentIds = await repository.QueryNoTracking<Deployment>(d => d.TaskId == taskId).Select(d => d.Id).ToListAsync().ConfigureAwait(false);

            return await repository.QueryNoTracking<DeploymentCompletion>(c => deploymentIds.Contains(c.DeploymentId)).Select(c => c.State).ToListAsync().ConfigureAwait(false);
        });

    private Task<int> AuditCountAsync(int taskId, EventCategory category)
        => Run<SquidDbContext, int>(db => db.Set<Event>().AsNoTracking().CountAsync(e => e.ServerTaskId == taskId && e.Category == category));

    /// <summary>
    /// Loses the acknowledgement of the first committed transaction that moved a server task into <c>target</c>: the commit has already happened on the
    /// server when the transient connection error is thrown, exactly once.
    /// </summary>
    private sealed class LostCommitAcknowledgement
    {
        private readonly string _target;
        private int _pending;
        private int _fired;

        private readonly string _connectionString;

        public LostCommitAcknowledgement(string target, string connectionString)
        {
            _target = target;
            _connectionString = connectionString;
            Spotter = new TargetWriteSpotter(this);
            Committer = new AcknowledgementLoser(this);
        }

        public IInterceptor Spotter { get; }

        public IInterceptor Committer { get; }

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public DbContextOptions<SquidDbContext> Options() => new DbContextOptionsBuilder<SquidDbContext>().UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().AddInterceptors(Spotter, Committer).Options;

        private void Observe(DbCommand command)
        {
            var sql = command.CommandText.Replace("\"", string.Empty);

            if (!sql.Contains("UPDATE server_task", StringComparison.OrdinalIgnoreCase)) return;

            if (command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, _target))) Interlocked.Exchange(ref _pending, 1);
        }

        private void AfterCommit()
        {
            if (Interlocked.Exchange(ref _pending, 0) == 0 || Interlocked.Exchange(ref _fired, 1) == 1) return;

            throw new NpgsqlException("Exception while reading from stream", new EndOfStreamException("the connection dropped before the commit was acknowledged"));
        }

        private sealed class TargetWriteSpotter(LostCommitAcknowledgement owner) : DbCommandInterceptor
        {
            public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
            {
                if (result > 0) owner.Observe(command);

                return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
            }
        }

        private sealed class AcknowledgementLoser(LostCommitAcknowledgement owner) : DbTransactionInterceptor
        {
            public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                owner.AfterCommit();

                return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
            }
        }
    }

    private sealed class SucceedingAgent : IAsyncScriptService
    {
        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => Task.FromResult(Resp(command.ScriptTicket));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => Task.FromResult(Resp(request.Ticket));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Task.FromResult(Resp(command.Ticket));

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
