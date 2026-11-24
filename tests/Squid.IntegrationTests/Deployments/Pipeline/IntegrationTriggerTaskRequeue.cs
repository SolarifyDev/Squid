using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;

namespace Squid.IntegrationTests.Deployments.Pipeline;

public class IntegrationTriggerTaskRequeue : TriggerDeploymentFixtureBase
{
    [Fact]
    public async Task Redelivery_OfOwnerlessCancellingTask_ReleasesEnvironmentForWaitingDeployment()
    {
        var command = await SeedReleaseAsync();
        var cancelledId = 0;
        var waitingId = 0;
        await Run<IDeploymentService, SquidDbContext, IServerTaskDataProvider>(async (deployments, db, tasks) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var cancelled = await deployments.CreateDeploymentForTriggerAsync(command);

            cancelledId = cancelled.TaskId;
            waitingId = (await deployments.CreateDeploymentForTriggerAsync(command)).TaskId;
            await tasks.TransitionStateAsync(cancelledId, TaskState.Pending, TaskState.Executing);
            // Represent a pre-lease running task. A normal claim now stamps a live owner.
            await db.Set<ServerTask>().Where(task => task.Id == cancelledId).ExecuteUpdateAsync(update => update
                .SetProperty(task => task.LeaseOwner, (string)null)
                .SetProperty(task => task.LeaseRenewedAt, (DateTimeOffset?)null));
            db.Set<DeploymentExecutionCheckpoint>().Add(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = cancelledId, DeploymentId = cancelled.Deployment.Id, LastCompletedBatchIndex = -1,
                OutputVariablesJson = "[]", BatchStatesJson = "{}", InFlightScriptsJson = "[]"
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        await Run<IServerTaskControlService>(control => control.CancelTaskAsync(cancelledId));
        var script = new CountingScriptProbe();
        var jobs = new MockSquidBackgroundJobClient();

        await Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(cancelledId, CancellationToken.None, false),
            b => b.RegisterInstance(script).As<ILocalProcessRunner>());
        script.Starts.ShouldBe(0);
        await Run<IServerTaskDataProvider, IDeploymentCheckpointService>(async (tasks, checkpoints) =>
        {
            (await tasks.GetServerTaskByIdNoTrackingAsync(cancelledId)).State.ShouldBe(TaskState.Cancelled);
            (await checkpoints.LoadAsync(cancelledId)).ShouldBeNull();
        });

        await Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(waitingId, CancellationToken.None, false), b =>
        {
            b.RegisterInstance(script).As<ILocalProcessRunner>();
            b.RegisterInstance(jobs).As<ISquidBackgroundJobClient>();
        });

        await Run<IServerTaskDataProvider>(async tasks =>
            (await tasks.GetServerTaskByIdNoTrackingAsync(waitingId)).State.ShouldBe(TaskState.Success));
        script.Starts.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyEnvironment_RequeuesTriggerEntryOrReportsMissingJob(bool queueUnavailable)
    {
        var command = await SeedReleaseAsync();
        var id = 0;
        var blocker = 0;
        await Run<IDeploymentService, SquidDbContext, IServerTaskDataProvider>(async (deployments, db, tasks) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            id = (await deployments.CreateDeploymentForTriggerAsync(command)).TaskId;
            blocker = (await deployments.CreateDeploymentForTriggerAsync(command)).TaskId;
            await tasks.TransitionStateAsync(blocker, TaskState.Pending, TaskState.Executing);
            await tx.CommitAsync();
        });

        Expression<Func<IDeploymentTaskExecutor, Task>> scheduled = null;
        var jobs = new Mock<ISquidBackgroundJobClient>();
        jobs.Setup(j => j.Schedule(It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(),
                It.IsAny<TimeSpan>(), It.IsAny<string>()))
            .Callback<Expression<Func<IDeploymentTaskExecutor, Task>>, TimeSpan, string>((e, _, _) => scheduled = e)
            .Returns(queueUnavailable ? null : "slot-job");
        var delivery = Run<IDeploymentTaskExecutor>(e => e.ProcessAsync(id, CancellationToken.None, false),
            b => b.RegisterInstance(jobs.Object));

        if (queueUnavailable) await Should.ThrowAsync<InvalidOperationException>(() => delivery);
        else await delivery;

        var call = (MethodCallExpression)scheduled.Body;

        call.Method.Name.ShouldBe(nameof(IDeploymentTaskExecutor.ProcessAsync));
        call.Arguments.Count.ShouldBe(3);
        Expression.Lambda<Func<bool>>(call.Arguments[2]).Compile()().ShouldBeFalse();
        await Run<SquidDbContext>(async db =>
        {
            var task = await db.Set<ServerTask>().AsNoTracking().SingleAsync(t => t.Id == id);
            task.State.ShouldBe(TaskState.Pending);
            if (queueUnavailable) task.JobId.ShouldBeNullOrEmpty();
            else task.JobId.ShouldBe("slot-job");
            (await db.Set<DeploymentCompletion>().CountAsync()).ShouldBe(0);
        });

        // The delayed job may arrive after another initial delivery already ran and paused.
        await Run<IServerTaskDataProvider>(async tasks =>
        {
            await tasks.TransitionStateAsync(blocker, TaskState.Executing, TaskState.Success);
            await tasks.TransitionStateAsync(id, TaskState.Pending, TaskState.Executing);
            await tasks.TransitionStateAsync(id, TaskState.Executing, TaskState.Paused);
        });
        await Run<IDeploymentTaskExecutor>(scheduled.Compile());
        await Run<IServerTaskDataProvider>(async tasks => (await tasks.GetServerTaskByIdNoTrackingAsync(id)).State.ShouldBe(TaskState.Paused));
    }

    [Fact]
    public async Task BusyEnvironment_RequeuesExplicitResumeAndResumesWhenSlotBecomesAvailable()
    {
        var command = await SeedReleaseAsync();
        var id = 0;
        var blocker = 0;
        await Run<IDeploymentService, SquidDbContext, IServerTaskDataProvider>(async (deployments, db, tasks) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            id = (await deployments.CreateDeploymentForTriggerAsync(command)).TaskId;
            blocker = (await deployments.CreateDeploymentForTriggerAsync(command)).TaskId;
            await tasks.TransitionStateAsync(blocker, TaskState.Pending, TaskState.Executing);
            await tx.CommitAsync();
        });
        Expression<Func<IDeploymentTaskExecutor, Task>> scheduled = null;
        var jobs = new Mock<ISquidBackgroundJobClient>();
        jobs.Setup(j => j.Schedule(It.IsAny<Expression<Func<IDeploymentTaskExecutor, Task>>>(),
                It.IsAny<TimeSpan>(), It.IsAny<string>()))
            .Callback<Expression<Func<IDeploymentTaskExecutor, Task>>, TimeSpan, string>((e, _, _) => scheduled = e)
            .Returns("resume-slot-job");
        var script = new CountingScriptProbe();

        await Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(id, CancellationToken.None, true),
            b => b.RegisterInstance(jobs.Object));
        var call = (MethodCallExpression)scheduled.Body;

        call.Method.Name.ShouldBe(nameof(IDeploymentTaskExecutor.ProcessAsync));
        call.Arguments.Count.ShouldBe(3);
        Expression.Lambda<Func<bool>>(call.Arguments[2]).Compile()().ShouldBeTrue();
        await Run<IServerTaskDataProvider>(async tasks =>
        {
            await tasks.TransitionStateAsync(blocker, TaskState.Executing, TaskState.Success);
            await tasks.TransitionStateAsync(id, TaskState.Pending, TaskState.Executing);
            await tasks.TransitionStateAsync(id, TaskState.Executing, TaskState.Paused);
        });

        await Run<IDeploymentTaskExecutor>(scheduled.Compile(),
            b => b.RegisterInstance(script).As<ILocalProcessRunner>());

        await Run<IServerTaskDataProvider>(async tasks =>
            (await tasks.GetServerTaskByIdNoTrackingAsync(id)).State.ShouldBe(TaskState.Success));
        script.Starts.ShouldBe(1);
    }

    private sealed class CountingScriptProbe : ILocalProcessRunner
    {
        private int _starts;
        public int Starts => Volatile.Read(ref _starts);

        public Task<ScriptExecutionResult> RunAsync(string executable, string arguments, string workDir,
            CancellationToken ct, TimeSpan? timeout, SensitiveValueMasker masker, Dictionary<string, string> environmentVariables)
        {
            Interlocked.Increment(ref _starts);

            return Task.FromResult(new ScriptExecutionResult { Success = true });
        }
    }
}
