using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Runtime;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.IntegrationTests.Services.ProjectTriggers.Runtime;

public partial class ProjectTriggerRuntimeIntegrationTests
{
    [Fact]
    public async Task LockedBusinessWriteRunsOnceAndASecondWorkerCanProcessAnotherRow()
    {
        var id = await SeedTriggerAsync();
        _evaluate = (_, _, _) => Task.FromResult(new TriggerEvaluation("v2", [Occurrence("a"), Occurrence("b")], "Matched."));
        await ObserveAsync(id);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _execute = async (db, actor, _) =>
        {
            db.Database.CurrentTransaction.ShouldNotBeNull();
            actor.Id.ShouldBe(Owner);
            actor.IsInternal.ShouldBeFalse();
            var sequence = Interlocked.Increment(ref calls);
            if (sequence == 1)
            {
                started.SetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            var release = new Release { ProjectId = 1, ChannelId = 3, SpaceId = 1, Version = $"1.0.{sequence}" };
            db.Add(release);
            await db.SaveChangesAsync();
            return new TriggerActionResult(release.Id);
        };
        var first = ExecuteAsync(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (await ExecuteAsync(1).WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(1);
        }
        finally
        {
            resume.SetResult();
        }
        (await first).ShouldBe(1);
        (await ExecuteAsync()).ShouldBe(0);

        await Run<SquidDbContext>(async db =>
        {
            var releaseIds = await db.Set<Release>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync();
            var runs = await db.Set<ProjectTriggerRun>().OrderBy(r => r.ReleaseId).ToListAsync();
            runs.Select(r => r.ReleaseId!.Value).ShouldBe(releaseIds);
            runs.ShouldAllBe(r => r.Status == ProjectTriggerRunStatuses.ReleaseCreated && r.Attempts == 1);
        });
    }

    [Fact]
    public async Task BusinessFailureAfterSaveRollsBackWritesAndResumesAtTheSavedPlan()
    {
        await ObserveAsync(await SeedTriggerAsync());
        var create = _execute;
        var fail = true;
        _execute = async (db, actor, plan) =>
        {
            plan.ChannelId.ShouldBe(3);
            var result = await create(db, actor, plan);
            if (fail) throw new TimeoutException("private diagnostic token");
            return result;
        };

        (await ExecuteAsync()).ShouldBe(1);
        var run = await ReadRunAsync();
        run.Status.ShouldBe(ProjectTriggerRunStatuses.Pending);
        run.Attempts.ShouldBe(1);
        run.NextAttemptAt.ShouldBeGreaterThan(_clock.GetUtcNow());
        run.Reason.ShouldNotContain("private diagnostic token");
        run.ReleaseId.ShouldBeNull();
        (await ExecuteAsync()).ShouldBe(0);
        await Run<SquidDbContext>(async db => (await db.Set<Release>().CountAsync()).ShouldBe(0));

        fail = false;
        _clock.Advance(TimeSpan.FromDays(1));
        (await ExecuteAsync()).ShouldBe(1);
        var completed = await ReadRunAsync();
        completed.Status.ShouldBe(ProjectTriggerRunStatuses.ReleaseCreated);
        completed.ReleaseId.ShouldNotBeNull();
        completed.Attempts.ShouldBe(2);
        await Run<SquidDbContext>(async db => (await db.Set<Release>().CountAsync()).ShouldBe(1));
    }

    [Fact]
    public async Task CancellationAfterFlushedBusinessWritesRollsBackTheSharedRunTransaction()
    {
        await ObserveAsync(await SeedTriggerAsync());
        using var cancellation = new CancellationTokenSource();
        var create = _execute;

        _execute = async (db, actor, plan) =>
        {
            await create(db, actor, plan);
            cancellation.Cancel();

            throw new OperationCanceledException(cancellation.Token);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => InScopeAsync<IProjectTriggerWorker, int>(
            worker => worker.ExecutePendingAsync(10, cancellation.Token)));

        var run = await ReadRunAsync();

        run.Status.ShouldBe(ProjectTriggerRunStatuses.Pending);
        run.Attempts.ShouldBe(0);
        run.ReleaseId.ShouldBeNull();

        await Run<SquidDbContext>(async db => (await db.Set<Release>().CountAsync()).ShouldBe(0));
    }

    [Theory]
    [InlineData(false, ProjectTriggerRunStatuses.ReleaseCreated)]
    [InlineData(true, ProjectTriggerRunStatuses.Skipped)]
    public async Task NameEditPreservesPendingWorkButAnExecutableChangeAndRevertStillInvalidatesIt(bool changeAndRevert, string expected)
    {
        var id = await SeedTriggerAsync();
        await ObserveAsync(id);
        await Run<SquidDbContext>(async db =>
        {
            var trigger = await db.Set<ProjectTrigger>().SingleAsync();
            trigger.Name = "Updated name";
            trigger.Revision++;
            if (changeAndRevert)
            {
                trigger.IsDisabled = true;
                trigger.ExecutionRevision = trigger.Revision;
                await db.SaveChangesAsync();
                trigger.IsDisabled = false;
                trigger.ExecutionRevision = ++trigger.Revision;
            }
            await db.SaveChangesAsync();
        });

        (await ExecuteAsync()).ShouldBe(1);
        (await ReadRunAsync()).Status.ShouldBe(expected);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("revision")]
    [InlineData("owner")]
    public async Task DefinitionChangesBeforeExecutionLeaveAnExplainedSkip(string change)
    {
        var id = await SeedTriggerAsync();
        await ObserveAsync(id);
        await Run<SquidDbContext>(async db =>
        {
            var trigger = await db.Set<ProjectTrigger>().SingleAsync();
            if (change == "disabled") trigger.IsDisabled = true;
            if (change == "deleted") trigger.IsDeleted = true;
            if (change == "revision") trigger.ExecutionRevision = ++trigger.Revision;
            if (change == "owner") trigger.ExecutionUserId++;
            await db.SaveChangesAsync();
        });

        (await ExecuteAsync()).ShouldBe(1);
        var run = await ReadRunAsync();
        run.Status.ShouldBe(ProjectTriggerRunStatuses.Skipped);
        run.Reason.ShouldNotBeNullOrWhiteSpace();
        run.ReleaseId.ShouldBeNull();
        await Run<SquidDbContext>(async db => (await db.Set<Release>().AnyAsync()).ShouldBeFalse());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevokedPermissionOrAPlanOutsideTheAllowedScopePreventsBusinessWrites(bool revoked)
    {
        await ObserveAsync(await SeedTriggerAsync());
        if (revoked)
        {
            var resources = new TriggerResourceScope(3);
            _access.Setup(a => a.EnsureActionAsync(Owner, It.IsAny<ProjectTriggerDefinition>(), resources, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PermissionDeniedException(Permission.ReleaseCreate, "private permission data"));
        }
        else
        {
            _allowed = new TriggerResourceScope(4);
        }

        (await ExecuteAsync()).ShouldBe(1);
        var run = await ReadRunAsync();
        run.Status.ShouldBe(ProjectTriggerRunStatuses.Failed);
        run.Reason.ShouldNotBeNullOrWhiteSpace();
        run.Reason.ShouldNotContain("private permission data");
        run.Attempts.ShouldBe(1);
        await Run<SquidDbContext>(async db => (await db.Set<Release>().AnyAsync()).ShouldBeFalse());
    }

    private void CreateDeploymentWithTask()
    {
        var create = _execute;
        _execute = async (db, actor, plan) =>
        {
            var release = await create(db, actor, plan);
            var task = new ServerTask
            {
                Name = "Triggered deploy", Description = "Deploy", QueueTime = _clock.GetUtcNow(), State = TaskState.Pending,
                ServerTaskType = "Deploy", JSON = "{}", DataVersion = [], BusinessProcessState = "Pending", SpaceId = 1
            };
            db.Add(task);
            await db.SaveChangesAsync();
            var deployment = new Deployment { Name = "Test", ProjectId = 1, SpaceId = 1, ChannelId = 3, EnvironmentId = 4, TaskId = task.Id, ReleaseId = release.ReleaseId, Json = "{}" };
            db.Add(deployment);
            await db.SaveChangesAsync();
            return new TriggerActionResult(release.ReleaseId, deployment.Id, task.Id);
        };
    }

    [Fact]
    public async Task LostCommitAfterSuccessfulQueueAcceptanceRedeliversTheOriginalTask()
    {
        await ObserveAsync(await SeedTriggerAsync());
        CreateDeploymentWithTask();
        await ExecuteAsync();
        var committed = await ReadRunAsync();
        QueueWith(i => $"job-{i}");

        await Run<SquidDbContext>(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_trigger_dispatch_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected lost acknowledgement'; END; $$;
                CREATE CONSTRAINT TRIGGER fail_trigger_dispatch_commit AFTER UPDATE ON project_trigger_run
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.status = 'Dispatched') EXECUTE FUNCTION fail_trigger_dispatch_commit();
                """);
            try
            {
                await Should.ThrowAsync<Exception>(() => DispatchAsync());
                (await db.Set<ServerTask>().AsNoTracking().SingleAsync()).JobId.ShouldBeNull();
                (await ReadRunAsync()).Status.ShouldBe(ProjectTriggerRunStatuses.AwaitingDispatch);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_trigger_dispatch_commit ON project_trigger_run; DROP FUNCTION fail_trigger_dispatch_commit();");
            }
        });

        (await DispatchAsync()).ShouldBe(1);
        _queuedTasks.ShouldBe(new[] { committed.TaskId!.Value, committed.TaskId.Value });
        (await ReadRunAsync()).Status.ShouldBe(ProjectTriggerRunStatuses.Dispatched);
    }

    [Fact]
    public async Task ConsumerRequeueDuringQueueOfferKeepsItsJobIdWhenTheRunIsAcknowledged()
    {
        await ObserveAsync(await SeedTriggerAsync());
        CreateDeploymentWithTask();
        await ExecuteAsync();
        var committed = await ReadRunAsync();

        QueueWith(_ =>
        {
            using var scope = OpenScope();
            var db = scope.Resolve<SquidDbContext>();

            db.Set<ServerTask>().Where(task => task.Id == committed.TaskId)
                .ExecuteUpdate(set => set.SetProperty(task => task.JobId, "consumer-retry-job"));

            return "accepted-job";
        });

        (await DispatchAsync()).ShouldBe(1);
        (await ReadRunAsync()).Status.ShouldBe(ProjectTriggerRunStatuses.Dispatched);

        await Run<SquidDbContext>(async db =>
            (await db.Set<ServerTask>().SingleAsync()).JobId.ShouldBe("consumer-retry-job"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("accepted-before-error")]
    public async Task RetryDispatchReusesCommittedTaskEvenIfTriggerWasDisabledOrJobIdChanged(string first)
    {
        var id = await SeedTriggerAsync();
        await ObserveAsync(id);
        CreateDeploymentWithTask();
        (await ExecuteAsync()).ShouldBe(1);
        _queuedTasks.ShouldBeEmpty();
        var committed = await ReadRunAsync();
        committed.Status.ShouldBe(ProjectTriggerRunStatuses.AwaitingDispatch);
        QueueWith(attempt => attempt == 1 ? first == "empty" ? "" : throw new TimeoutException("accepted but response lost") : "job-after-retry");

        (await DispatchAsync()).ShouldBe(1);
        var waiting = await ReadRunAsync();
        waiting.Status.ShouldBe(ProjectTriggerRunStatuses.AwaitingDispatch);
        waiting.CompletedAt.ShouldBeNull();
        waiting.Reason.ShouldNotContain("accepted but response lost");
        (await DispatchAsync()).ShouldBe(0);
        _clock.Advance(TimeSpan.FromDays(1));
        await Run<SquidDbContext>(async db =>
        {
            await db.Set<ProjectTrigger>().Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDisabled, true));
            if (first == "accepted-before-error")
                await db.Set<ServerTask>().ExecuteUpdateAsync(s => s.SetProperty(t => t.JobId, "later-resume-job"));
        });

        (await DispatchAsync()).ShouldBe(1);
        (await ExecuteAsync()).ShouldBe(0);
        (await DispatchAsync()).ShouldBe(0);
        var dispatched = await ReadRunAsync();
        dispatched.Status.ShouldBe(ProjectTriggerRunStatuses.Dispatched);
        dispatched.CompletedAt.ShouldNotBeNull();
        _queuedTasks.ShouldBe(new[] { committed.TaskId!.Value, committed.TaskId.Value });
        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<Deployment>().SingleAsync()).Id.ShouldBe(dispatched.DeploymentId!.Value);
            (await db.Set<ServerTask>().SingleAsync()).JobId.ShouldBe(first == "empty" ? "job-after-retry" : "later-resume-job");
        });
    }
}
