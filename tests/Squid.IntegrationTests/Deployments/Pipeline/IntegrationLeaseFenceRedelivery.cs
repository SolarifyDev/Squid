using System.Linq.Expressions;
using Autofac;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.ServerTask;
using Squid.IntegrationTests.Base;
using Squid.Message.Contracts.Tentacle;
using static Squid.IntegrationTests.Deployments.Pipeline.IntegrationPausePreservesRemoteScript;

namespace Squid.IntegrationTests.Deployments.Pipeline;

/// <summary>
/// Fidelity: HIGH (real Postgres, the real runner with every real phase, the real lease fence, reaper and control service; only the agent RPC is faked).
/// A server dies mid-deployment and its job is redelivered while the dead owner's lease is still young: the fence drops the delivery (it cannot tell a dead owner
/// from a live one), and Hangfire will not deliver it again. The reaper must then be what resumes the task once the lease goes stale — and the resume must never
/// run beside a second delivery.
/// </summary>
public class IntegrationLeaseFenceRedelivery : DeploymentFixtureBase
{
    private const string DeadOwner = "dead-pod:1:abc/run";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RedeliveryFencedWhileTheDeadOwnersLeaseIsYoung_IsResumedByTheReaperOnceItGoesStale()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();
        agent.Finish();
        await CrashedOwnerAsync(seeded.TaskId, leaseAge: TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await RunAsync(seeded.TaskId, agent).ConfigureAwait(false);

        var fenced = await LoadAsync(seeded.TaskId).ConfigureAwait(false);
        fenced.State.ShouldBe(TaskState.Executing, "the redelivery arrived while the lease was young: it is dropped, the task untouched");
        fenced.LeaseOwner.ShouldBe(DeadOwner);
        agent.StartedTickets.ShouldBeEmpty();

        await AgeLeaseAsync(seeded.TaskId, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        var resumeJobs = await ReapAsync().ConfigureAwait(false);

        (await LoadAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(TaskState.Paused, "the reaper recovers the dead owner's task");
        resumeJobs.ShouldBe(1, "and requests the resume the dropped redelivery would have performed");

        await RunAsync(seeded.TaskId, agent).ConfigureAwait(false);

        (await LoadAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(TaskState.Success, "the resume job completes the deployment");
        agent.StartedTickets.Count.ShouldBe(1);
    }

    // The resume the reaper requested and a late redelivery of the original job reach the task together: exactly one pipeline may run.
    [Fact]
    public async Task TheReapersResume_AndALateRedelivery_RunExactlyOnePipeline()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var agent = new BlockingAgent();
        await CrashedOwnerAsync(seeded.TaskId, leaseAge: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        (await ReapAsync().ConfigureAwait(false)).ShouldBe(1);

        await Run<ILifetimeScope>(async scope =>
        {
            await using var resume = scope.BeginLifetimeScope();
            var resumed = BuildRunner(resume).ProcessAsync(seeded.TaskId, CancellationToken.None);
            await agent.Polled.WaitAsync(Bound).ConfigureAwait(false);

            await using var duplicate = scope.BeginLifetimeScope();
            await BuildRunner(duplicate).ProcessAsync(seeded.TaskId, CancellationToken.None).WaitAsync(Bound).ConfigureAwait(false);

            agent.StartedTickets.Count.ShouldBe(1, "the late redelivery must not start a second pipeline");
            (await LoadAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(TaskState.Executing, "nor disturb the running one");

            agent.Finish();
            await resumed.WaitAsync(Bound).ConfigureAwait(false);
        }, extraRegistration: b => b.RegisterInstance(new FixedClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance()).ConfigureAwait(false);

        (await LoadAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(TaskState.Success);
        agent.StartedTickets.Count.ShouldBe(1);
    }

    // The server stopped, the task was resumed by the reaper, and the resumed run stopped its server again: past MaxAutoResumes the task waits for an operator.
    [Fact]
    public async Task ATaskThatKeepsStoppingItsServer_IsResumedAutomaticallyOnlyUpToTheCap()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        var cap = ServerTaskLeaseOptions.DefaultMaxAutoResumes;

        for (var stop = 1; stop <= cap + 1; stop++)
        {
            await CrashedOwnerAsync(seeded.TaskId, leaseAge: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            var resumeJobs = await ReapAsync().ConfigureAwait(false);

            (await LoadAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(TaskState.Paused);
            resumeJobs.ShouldBe(stop <= cap ? 1 : 0, $"stop {stop} of the server");

            await MarkPausedTaskExecutingAsync(seeded.TaskId).ConfigureAwait(false);
        }
    }

    // An operator who sets MaxAutoResumes to 0 opts out: the reaper still pauses the stopped task, but nothing resumes it.
    [Fact]
    public async Task WithAutoResumeDisabled_AReaperPausedTask_StaysPaused()
    {
        var seeded = await SeedScriptDeploymentAsync(isRequired: true).ConfigureAwait(false);
        await CrashedOwnerAsync(seeded.TaskId, leaseAge: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        var queued = 0;

        await Run<IServerTaskLeaseReaperService, ISquidBackgroundJobClient>(async (reaper, jobs) =>
        {
            await reaper.ReapStaleAsync().ConfigureAwait(false);

            queued = ((MockSquidBackgroundJobClient)jobs).EnqueuedJobs.OfType<Expression<Func<IDeploymentTaskExecutor, Task>>>().Count();
        }, extraRegistration: b => b.RegisterInstance(new ServerTaskSetting { Lease = new ServerTaskLeaseSettings { MaxAutoResumes = 0 } }).AsSelf().SingleInstance()).ConfigureAwait(false);

        (await LoadAsync(seeded.TaskId).ConfigureAwait(false)).State.ShouldBe(TaskState.Paused);
        queued.ShouldBe(0, "0 means never resume automatically");
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

    private Task RunAsync(int taskId, IAsyncScriptService agent)
        => Run<ILifetimeScope>(scope => BuildRunner(scope).ProcessAsync(taskId, CancellationToken.None).WaitAsync(Bound), extraRegistration: b => b.RegisterInstance(new FixedClientFactory(agent)).As<IHalibutClientFactory>().SingleInstance());

    // The reaper through the container, with the control service it resumes through; returns how many resume jobs it queued for the task executor.
    private async Task<int> ReapAsync()
    {
        var queued = 0;

        await Run<IServerTaskLeaseReaperService, ISquidBackgroundJobClient>(async (reaper, jobs) =>
        {
            await reaper.ReapStaleAsync().ConfigureAwait(false);

            queued = ((MockSquidBackgroundJobClient)jobs).EnqueuedJobs.OfType<Expression<Func<IDeploymentTaskExecutor, Task>>>().Count();
        }).ConfigureAwait(false);

        return queued;
    }

    // The previous server died: the row is Executing under an owner whose lease stopped renewing.
    private Task CrashedOwnerAsync(int taskId, TimeSpan leaseAge)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(
            t => t.Id == taskId,
            s => s.SetProperty(t => t.State, TaskState.Executing).SetProperty(t => t.LeaseOwner, DeadOwner).SetProperty(t => t.LeaseRenewedAt, DateTimeOffset.UtcNow - leaseAge),
            CancellationToken.None));

    private Task AgeLeaseAsync(int taskId, TimeSpan age)
        => Run<IRepository>(r => r.ExecuteUpdateAsync<ServerTask>(t => t.Id == taskId, s => s.SetProperty(t => t.LeaseRenewedAt, DateTimeOffset.UtcNow - age), CancellationToken.None));

    // The resume job claimed the paused task and its server stopped again.
    private Task MarkPausedTaskExecutingAsync(int taskId) => CrashedOwnerAsync(taskId, TimeSpan.FromMinutes(10));

    private Task<ServerTask> LoadAsync(int taskId) => Run<IServerTaskDataProvider, ServerTask>(p => p.GetServerTaskByIdNoTrackingAsync(taskId, CancellationToken.None));
}
