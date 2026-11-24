using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Core.Services.Jobs;

namespace Squid.IntegrationTests.Deployments.Pipeline;

public class IntegrationTriggerTaskExecution : TriggerDeploymentFixtureBase
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task DuplicateDelivery_WhileScriptRuns_StartsOnceAndPreservesOwnerCancellation(bool cancel, bool remoteCancel, bool finishImmediately)
    {
        var created = await CreateTriggerDeploymentAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptProbe(async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        });

        var owner = DeliverAsync(created.TaskId, script);
        try
        {
            await Task.WhenAny(owner, started.Task).WaitAsync(TimeSpan.FromSeconds(15));
            if (owner.IsCompleted) await owner;
            started.Task.IsCompletedSuccessfully.ShouldBeTrue();

            await DeliverAsync(created.TaskId, script).WaitAsync(TimeSpan.FromSeconds(5));
            script.Starts.ShouldBe(1);
            (await ReadStateAsync(created.TaskId)).ShouldBe(TaskState.Executing);

            if (cancel)
            {
                await Run<IServerTaskControlService>(control => control.CancelTaskAsync(created.TaskId), b =>
                {
                    if (remoteCancel) b.RegisterInstance(new TaskCancellationRegistry()).As<ITaskCancellationRegistry>();
                });
                if (finishImmediately) release.TrySetResult();
                await owner.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            release.TrySetResult();
            await owner;
        }

        (await ReadStateAsync(created.TaskId)).ShouldBe(cancel ? TaskState.Cancelled : TaskState.Success);
        await DeliverAsync(created.TaskId, script);
        script.Starts.ShouldBe(1);
        await Run<SquidDbContext>(async db =>
            (await db.Set<DeploymentCompletion>().CountAsync(c => c.DeploymentId == created.Deployment.Id)).ShouldBe(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialRedelivery_WhilePaused_WaitsForExplicitResume(bool isTriggerTask)
    {
        var created = await CreateTriggerDeploymentAsync();
        var id = created.TaskId;
        var script = new ScriptProbe();
        if (!isTriggerTask)
            await Run<SquidDbContext>(db => db.Set<ServerTask>().Where(task => task.Id == id)
                .ExecuteUpdateAsync(update => update.SetProperty(task => task.IsTriggerTask, false)));
        await Run<IServerTaskDataProvider>(async tasks =>
        {
            await tasks.TransitionStateAsync(id, TaskState.Pending, TaskState.Executing);
            await tasks.TransitionStateAsync(id, TaskState.Executing, TaskState.Paused);
        });

        await DeliverAsync(id, script);
        (await ReadStateAsync(id)).ShouldBe(TaskState.Paused);
        script.Starts.ShouldBe(0);

        var jobs = new MockSquidBackgroundJobClient();
        await Run<IServerTaskControlService>(control => control.ResumeTaskAsync(id),
            b => b.RegisterInstance(jobs).As<ISquidBackgroundJobClient>());
        var resume = (Expression<Func<IDeploymentTaskExecutor, Task>>)jobs.EnqueuedJobs.Single();
        await Run<IDeploymentTaskExecutor>(resume.Compile(), b => b.RegisterInstance(script).As<ILocalProcessRunner>());

        (await ReadStateAsync(id)).ShouldBe(TaskState.Success);
        script.Starts.ShouldBe(1);
    }

    [Fact]
    public async Task ResumeAcknowledgement_AfterConsumerAdvances_PreservesNewerJobAndState()
    {
        var created = await CreateTriggerDeploymentAsync();
        var id = created.TaskId;
        await Run<IServerTaskDataProvider>(async tasks =>
        {
            await tasks.TransitionStateAsync(id, TaskState.Pending, TaskState.Executing);
            await tasks.TransitionStateAsync(id, TaskState.Executing, TaskState.Paused);
            await tasks.SetJobIdAsync(id, "old-job");
        });
        var jobs = new Mock<ISquidBackgroundJobClient>();
        jobs.Setup(j => j.Enqueue(It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(), It.IsAny<string>()))
            .Returns(() =>
            {
                Task.Run(() => Run<IServerTaskDataProvider>(async tasks =>
                {
                    await tasks.TransitionStateAsync(id, TaskState.Paused, TaskState.Executing);
                    await tasks.SetJobIdAsync(id, "newer-job");
                })).GetAwaiter().GetResult();
                return "resume-job";
            });

        await Run<IServerTaskControlService>(control => control.ResumeTaskAsync(id), b => b.RegisterInstance(jobs.Object));

        await Run<IServerTaskDataProvider>(async tasks =>
        {
            var task = await tasks.GetServerTaskByIdNoTrackingAsync(id);
            task.State.ShouldBe(TaskState.Executing);
            task.JobId.ShouldBe("newer-job");
        });
    }

    [Theory]
    [InlineData(TaskState.Cancelled)]
    [InlineData(TaskState.Cancelling)]
    public async Task InitialRedelivery_AfterUnownedTaskIsCancelled_NeverStartsAScript(string state)
    {
        var created = await CreateTriggerDeploymentAsync();
        var id = created.TaskId;
        if (state == TaskState.Cancelling) await SetExecutingWithoutOwnerAsync(id);
        await Run<IServerTaskControlService>(control => control.CancelTaskAsync(id));
        (await ReadStateAsync(id)).ShouldBe(state);
        var script = new ScriptProbe();

        await DeliverAsync(id, script);

        script.Starts.ShouldBe(0);
        (await ReadStateAsync(id)).ShouldBe(TaskState.Cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redelivery_AfterOwnerDisappearsWhileCancelling_LeavesCancellationForReaper(bool resumeDelivery)
    {
        var created = await CreateTriggerDeploymentAsync();
        var id = created.TaskId;
        var script = new ScriptProbe();
        await SetExecutingWithExpiredLeaseAsync(id);
        await Run<IDeploymentCheckpointService>(checkpoints => checkpoints.EnsureExistsAsync(id, created.Deployment.Id));
        await Run<IServerTaskControlService>(control => control.CancelTaskAsync(id));

        await DeliverAsync(id, script, resumeDelivery);

        (await ReadStateAsync(id)).ShouldBe(TaskState.Cancelling);
        await Run<IDeploymentCheckpointService>(async checkpoints => (await checkpoints.LoadAsync(id)).ShouldNotBeNull());
        await Run<IServerTaskLeaseReaperService>(reaper => reaper.ReapStaleAsync());
        await AssertCancellationCompletedAsync(id);
        await DeliverAsync(id, script);
        await DeliverAsync(id, script, resumeDelivery: true);
        script.Starts.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WorkerShutdown_PreservesCheckpointAndResumesOnDefaultRedelivery(bool resumeDelivery, bool isTriggerTask)
    {
        var created = await CreateTriggerDeploymentAsync();
        var id = created.TaskId;
        if (!isTriggerTask)
            await Run<SquidDbContext>(db => db.Set<ServerTask>().Where(task => task.Id == id)
                .ExecuteUpdateAsync(update => update.SetProperty(task => task.IsTriggerTask, false)));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        var script = new ScriptProbe(async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        var delivery = DeliverAsync(id, script, resumeDelivery, shutdown.Token);
        int checkpointId;

        try
        {
            await Task.WhenAny(delivery, started.Task).WaitAsync(TimeSpan.FromSeconds(15));
            if (delivery.IsCompleted) await delivery;
            started.Task.IsCompletedSuccessfully.ShouldBeTrue();
            script.LastToken.CanBeCanceled.ShouldBeTrue();
            checkpointId = await Run<IDeploymentCheckpointService, int>(async checkpoints =>
                (await checkpoints.LoadAsync(id)).ShouldNotBeNull().Id);

            await shutdown.CancelAsync();
            await script.Cancelled.WaitAsync(TimeSpan.FromSeconds(5));
            script.LastToken.IsCancellationRequested.ShouldBeTrue();
            await Should.ThrowAsync<OperationCanceledException>(() => delivery.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            release.TrySetResult();
            try { await delivery; }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }

        script.Cancellations.ShouldBe(1);
        await Run<IServerTaskDataProvider, IDeploymentCheckpointService>(async (tasks, checkpoints) =>
        {
            var task = await tasks.GetServerTaskByIdNoTrackingAsync(id);
            task.State.ShouldBe(TaskState.Paused);
            task.ResumeOnRedelivery.ShouldBeTrue();
            (await checkpoints.LoadAsync(id)).ShouldNotBeNull().Id.ShouldBe(checkpointId);
        });
        await Run<SquidDbContext>(async db =>
            (await db.Set<DeploymentCompletion>().AnyAsync(c => c.DeploymentId == created.Deployment.Id)).ShouldBeFalse());

        await DeliverAsync(id, script);

        (await ReadStateAsync(id)).ShouldBe(TaskState.Success);
        script.Starts.ShouldBe(2);
    }

    [Fact]
    public async Task Cancellation_BetweenAdmissionAndLoadTask_CompletesWithoutStartingScript()
    {
        var created = await CreateTriggerDeploymentAsync();
        var id = created.TaskId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new ScriptProbe();
        await SetExecutingWithoutOwnerAsync(id);
        await Run<IDeploymentCheckpointService>(checkpoints => checkpoints.EnsureExistsAsync(id, created.Deployment.Id));
        var delivery = Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(id, CancellationToken.None, false), b =>
        {
            b.RegisterInstance(script).As<ILocalProcessRunner>();
            b.RegisterInstance(new BeforeLoadTaskGate(entered, release)).As<IDeploymentPipelinePhase>();
        });

        try
        {
            await Task.WhenAny(delivery, entered.Task).WaitAsync(TimeSpan.FromSeconds(15));
            if (delivery.IsCompleted) await delivery;
            entered.Task.IsCompletedSuccessfully.ShouldBeTrue();
            await Run<IServerTaskControlService>(control => control.CancelTaskAsync(id),
                b => b.RegisterInstance(new TaskCancellationRegistry()).As<ITaskCancellationRegistry>());
            release.TrySetResult();
            await delivery.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            release.TrySetResult();
            await delivery;
        }

        await AssertCancellationCompletedAsync(id);
        await DeliverAsync(id, script);
        script.Starts.ShouldBe(0);
    }

    [Fact]
    public async Task Redelivery_AfterWorkerDisappears_ResumesFromPersistedCheckpoint()
    {
        var created = await CreateTriggerDeploymentAsync();
        var id = created.TaskId;
        await SetExecutingWithExpiredLeaseAsync(id);
        await Run<IDeploymentCheckpointService>(checkpoints => checkpoints.SaveAsync(new DeploymentExecutionCheckpoint
        {
            ServerTaskId = id, DeploymentId = created.Deployment.Id, LastCompletedBatchIndex = 0,
            OutputVariablesJson = "[]", BatchStatesJson = "{}", InFlightScriptsJson = "[]"
        }));
        var script = new ScriptProbe();

        await DeliverAsync(id, script);

        (await ReadStateAsync(id)).ShouldBe(TaskState.Success);
        script.Starts.ShouldBe(0);
    }

    private Task SetExecutingWithoutOwnerAsync(int id) =>
        Run<SquidDbContext>(db => db.Set<ServerTask>().Where(task => task.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(task => task.State, TaskState.Executing)
            .SetProperty(task => task.LeaseOwner, (string)null)
            .SetProperty(task => task.LeaseRenewedAt, (DateTimeOffset?)null)));

    private Task SetExecutingWithExpiredLeaseAsync(int id) =>
        Run<SquidDbContext>(db => db.Set<ServerTask>().Where(task => task.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(task => task.State, TaskState.Executing)
            .SetProperty(task => task.LeaseOwner, "departed-worker/run")
            .SetProperty(task => task.LeaseRenewedAt, DateTimeOffset.UtcNow.AddHours(-1))));

    private Task DeliverAsync(int taskId, ScriptProbe script, bool resumeDelivery = false, CancellationToken ct = default) =>
        Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(taskId, ct, resumeDelivery),
            b => b.RegisterInstance(script).As<ILocalProcessRunner>());

    private Task<string> ReadStateAsync(int taskId) => Run<IServerTaskDataProvider, string>(async tasks =>
        (await tasks.GetServerTaskByIdNoTrackingAsync(taskId)).State);

    private async Task AssertCancellationCompletedAsync(int taskId)
    {
        await Run<IServerTaskDataProvider, IDeploymentCheckpointService>(async (tasks, checkpoints) =>
        {
            var task = await tasks.GetServerTaskByIdNoTrackingAsync(taskId);

            task.State.ShouldBe(TaskState.Cancelled);
            task.ConcurrencyTag.ShouldNotBeNullOrEmpty();
            (await tasks.HasActiveTaskWithTagAsync(task.ConcurrencyTag, 0)).ShouldBeFalse();
            (await checkpoints.LoadAsync(taskId)).ShouldBeNull();
        });
    }

    private sealed class BeforeLoadTaskGate(TaskCompletionSource entered, TaskCompletionSource release) : IDeploymentPipelinePhase
    {
        public int Order => 99;

        public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
    }

    private sealed class ScriptProbe(Func<CancellationToken, Task> run = null) : ILocalProcessRunner
    {
        private int _starts;
        private int _cancellations;
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts => Volatile.Read(ref _starts);
        public int Cancellations => Volatile.Read(ref _cancellations);
        public CancellationToken LastToken { get; private set; }
        public Task Cancelled => _cancelled.Task;

        public async Task<ScriptExecutionResult> RunAsync(string executable, string arguments, string workDir,
            CancellationToken ct, TimeSpan? timeout, SensitiveValueMasker masker, Dictionary<string, string> environmentVariables)
        {
            LastToken = ct;
            Interlocked.Increment(ref _starts);

            try
            {
                if (run != null) await run(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellations);
                _cancelled.TrySetResult();
                throw;
            }

            return new ScriptExecutionResult { Success = true };
        }
    }
}
