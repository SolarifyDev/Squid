using Autofac;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Message.Models.Deployments.ServerTask;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.Deployments.Concurrency;

/// <summary>
/// Real-Postgres coverage of the task lease and the stale-task reaper. The claim stamps the lease atomically; renewal and recovery are
/// both conditional UPDATEs on state + owner (+ staleness for recovery), so whichever of a live owner's renewal and the reaper reaches the
/// row first wins and the other no-ops. A task nobody leased is never recovered. Cancelling→Cancelled frees the environment's concurrency slot;
/// Executing→Paused keeps the checkpoint.
/// </summary>
public class IntegrationServerTaskLease : TestBase
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    private static readonly string Owner = ServerTaskLeaseOwner.Current;
    private const string DeadOwner = "dead-pod:1:abc";

    public IntegrationServerTaskLease() : base("ServerTaskLease", "squid_it_server_task_lease")
    {
    }

    [Fact]
    public async Task Claim_StampsTheLeaseWithThisProcess_OnTheDatabaseClock()
    {
        var id = await SeedAsync(TaskState.Pending, "deploy:lease-1");

        await Run<IServerTaskDataProvider>(p => p.TransitionStateAsync(id, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);

        var task = await LoadAsync(id);
        task.LeaseOwner.ShouldBe(Owner);
        task.LeaseRenewedAt.ShouldNotBeNull();
        (DateTimeOffset.UtcNow - task.LeaseRenewedAt.Value).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(TaskState.Paused, true)]       // a Paused row has no live owner
    [InlineData(TaskState.Success, true)]
    [InlineData(TaskState.Failed, true)]
    [InlineData(TaskState.Cancelling, false)]  // a cancel request does not end the pipeline: it keeps renewing until it finishes
    public async Task LeavingExecuting_ClearsTheLease_ExceptIntoCancelling(string next, bool cleared)
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:lease-2", leaseOwner: Owner, leaseAge: TimeSpan.Zero);

        await Run<IServerTaskDataProvider>(p => p.TransitionStateAsync(id, TaskState.Executing, next)).ConfigureAwait(false);

        var task = await LoadAsync(id);
        (task.LeaseOwner == null).ShouldBe(cleared);
        (task.LeaseRenewedAt == null).ShouldBe(cleared);
    }

    [Fact]
    public async Task Resume_PausedToExecuting_StampsAFreshLease()
    {
        var id = await SeedAsync(TaskState.Paused, "deploy:lease-3");

        await Run<IServerTaskDataProvider>(p => p.TransitionStateAsync(id, TaskState.Paused, TaskState.Executing)).ConfigureAwait(false);

        (await LoadAsync(id)).LeaseOwner.ShouldBe(Owner);
    }

    // The real container, the real keeper, the real database: starting a lease takes over a running task whose previous owner died, and releasing it stops.
    [Fact]
    public async Task Keeper_FromTheContainer_TakesOverARunningTask_AndReleasesIt()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:k-1", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(30));

        await Run<IServerTaskLeaseKeeper>(async keeper =>
        {
            await using var lease = keeper.Start(id);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while ((await LoadAsync(id)).LeaseOwner != Owner && DateTime.UtcNow < deadline) await Task.Delay(50).ConfigureAwait(false);

            lease.IsLost.ShouldBeFalse();
        }).ConfigureAwait(false);

        var task = await LoadAsync(id);
        task.LeaseOwner.ShouldBe(Owner, "the keeper took the lease over from the dead owner");
        (DateTimeOffset.UtcNow - task.LeaseRenewedAt.Value).ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Renew_OnlyByTheOwner_OnlyOnARunningTask()
    {
        var running = await SeedAsync(TaskState.Executing, "deploy:r-1", leaseOwner: Owner, leaseAge: TimeSpan.FromMinutes(2));
        var paused = await SeedAsync(TaskState.Paused, "deploy:r-2", leaseOwner: Owner, leaseAge: TimeSpan.FromMinutes(2));

        (await Renew(running, "someone-else")).ShouldBe(0);
        (await Renew(paused, Owner)).ShouldBe(0);
        (await Renew(running, Owner)).ShouldBe(1);

        var renewed = await LoadAsync(running);
        (DateTimeOffset.UtcNow - renewed.LeaseRenewedAt.Value).ShouldBeLessThan(TimeSpan.FromSeconds(30), "renewal moves the lease to now");
    }

    // The takeover rule: this process's own lease, no lease at all, or a holder that went silent. A live foreign owner is never displaced.
    [Theory]
    [InlineData(TaskState.Executing, "foreign", 1, 0)]       // live foreign owner (fresh lease): refused
    [InlineData(TaskState.Cancelling, "foreign", 1, 0)]
    [InlineData(TaskState.Executing, "foreign", 10, 1)]      // dead owner (stale lease): takeover
    [InlineData(TaskState.Cancelling, "foreign", 10, 1)]
    [InlineData(TaskState.Executing, "none", 10, 1)]         // pre-lease row untouched for longer than the stale window: takeover
    [InlineData(TaskState.Executing, "none", 0, 0)]          // pre-lease row claimed a moment ago: its (older-server) owner may be running it right now
    [InlineData(TaskState.Executing, "self", 0, 1)]          // same-process re-entry
    [InlineData(TaskState.Paused, "none", 0, 0)]
    [InlineData(TaskState.Pending, "none", 0, 0)]
    public async Task Acquire_FollowsTheTakeoverRule(string state, string holder, int ageMinutes, int expectedRows)
    {
        var owner = holder == "foreign" ? DeadOwner : holder == "self" ? Owner : null;
        var id = await SeedAsync(state, "deploy:a-1", leaseOwner: owner, leaseAge: TimeSpan.FromMinutes(ageMinutes), modifiedAgo: holder == "none" ? TimeSpan.FromMinutes(ageMinutes) : null);

        var rows = await Run<IServerTaskLeaseDataProvider, int>(p => p.AcquireAsync(id, Owner, StaleAfter));

        rows.ShouldBe(expectedRows);
        (await LoadAsync(id)).LeaseOwner.ShouldBe(expectedRows == 1 ? Owner : owner);
    }

    // The fence at the claim: a duplicate delivery of a task a live process still runs must not start a second pipeline.
    [Fact]
    public async Task StartExecuting_OnARowALiveForeignOwnerRuns_IsRefused_AndChangesNothing()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:fence-1", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromSeconds(10));
        var before = await LoadAsync(id);

        var ex = await Run<IServerTaskService, ServerTaskOwnedByLiveProcessException>(s => Should.ThrowAsync<ServerTaskOwnedByLiveProcessException>(() => s.StartExecutingAsync(id)));

        ex.ServerTaskId.ShouldBe(id);
        ex.LeaseOwner.ShouldBe(DeadOwner);
        var after = await LoadAsync(id);
        after.State.ShouldBe(TaskState.Executing);
        after.LeaseOwner.ShouldBe(DeadOwner);
        after.LeaseRenewedAt.ShouldBe(before.LeaseRenewedAt);
    }

    [Theory]
    [InlineData("foreign-stale")]
    [InlineData("none")]
    [InlineData("self-stale")]
    public async Task StartExecuting_OnAnExecutingRow_TakesOverWhenTheOwnerIsDeadOrLongUnleased_AsAResume(string holder)
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:fence-2", leaseOwner: holder == "none" ? null : holder == "self-stale" ? Owner : DeadOwner, leaseAge: TimeSpan.FromMinutes(10), modifiedAgo: holder == "none" ? TimeSpan.FromMinutes(10) : null);

        var result = await Run<IServerTaskService, StartExecutingResult>(s => s.StartExecutingAsync(id));

        result.Task.State.ShouldBe(TaskState.Executing);
        result.IsResumed.ShouldBeTrue("a takeover continues a deployment the dead owner already started");
        (await LoadAsync(id)).LeaseOwner.ShouldBe(Owner);
    }

    // A pod that predates leases claims a task with no lease and keeps running it without renewing anything. A duplicate delivery to a new pod a moment
    // later must not read "no lease" as "dead owner" and start a second pipeline beside it.
    [Fact]
    public async Task StartExecuting_OnAFreshRowWithNoLease_IsRefused_BecauseAnOlderServerMayBeRunningIt()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:fence-prelease");

        await Run<IServerTaskService>(s => Should.ThrowAsync<ServerTaskOwnedByLiveProcessException>(() => s.StartExecutingAsync(id))).ConfigureAwait(false);

        var row = await LoadAsync(id);
        row.State.ShouldBe(TaskState.Executing);
        row.LeaseOwner.ShouldBeNull("the refusal must not stamp a lease on the older server's task");
    }

    // A duplicate delivery to a task being cancelled used to hit the transition error, which the runner turns into Cancelling -> Failed on the owner's task.
    [Theory]
    [InlineData(1)]    // a live owner finishing the cancel
    [InlineData(10)]   // a dead owner: the reaper completes the cancel, a second pipeline must not start on it
    public async Task StartExecuting_OnACancellingTask_IsRefused_AndChangesNothing(int leaseAgeMinutes)
    {
        var id = await SeedAsync(TaskState.Cancelling, $"deploy:cancel-dup-{leaseAgeMinutes}", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(leaseAgeMinutes));
        var before = await LoadAsync(id);

        var ex = await Run<IServerTaskService, ServerTaskOwnedByLiveProcessException>(s => Should.ThrowAsync<ServerTaskOwnedByLiveProcessException>(() => s.StartExecutingAsync(id)));

        ex.LeaseOwner.ShouldBe(DeadOwner);
        var after = await LoadAsync(id);
        after.State.ShouldBe(TaskState.Cancelling);
        after.LeaseOwner.ShouldBe(DeadOwner);
        after.LeaseRenewedAt.ShouldBe(before.LeaseRenewedAt);
    }

    // The runner on the duplicate's side, against the real database: it must not fail the owner's Cancelling task.
    [Fact]
    public async Task Runner_OnADuplicateDeliveryToACancellingTask_LeavesItCancelling_AndRecordsNothing()
    {
        var id = await SeedAsync(TaskState.Cancelling, "deploy:runner-cancel-dup", leaseOwner: Owner, leaseAge: TimeSpan.FromSeconds(5));
        var completion = new Moq.Mock<Squid.Core.Services.DeploymentExecution.Pipeline.IDeploymentCompletionHandler>();
        var lifecycle = new Moq.Mock<Squid.Core.Services.DeploymentExecution.Lifecycle.IDeploymentLifecycle>();

        await Run<IServerTaskService, IServerTaskLeaseKeeper, IServerTaskDataProvider>(async (service, keeper, provider) =>
        {
            var phase = new Squid.Core.Services.DeploymentExecution.Pipeline.Phases.LoadTaskPhase(service, keeper);
            var runner = new Squid.Core.Services.DeploymentExecution.Pipeline.DeploymentPipelineRunner([phase], lifecycle.Object, completion.Object, new Squid.Core.Services.DeploymentExecution.Script.TaskCancellationRegistry(), provider, Moq.Mock.Of<Squid.Core.Services.Jobs.ISquidBackgroundJobClient>());

            await runner.ProcessAsync(id, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        completion.VerifyNoOtherCalls();
        (await LoadAsync(id)).State.ShouldBe(TaskState.Cancelling);
    }

    // A duplicate delivery inside the SAME process is as much a duplicate as one on another pod: this process's own fresh lease does not admit it.
    [Fact]
    public async Task StartExecuting_OnARowThisVeryProcessRunsWithAFreshLease_IsRefused()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:fence-2b", leaseOwner: Owner, leaseAge: TimeSpan.FromSeconds(5));

        await Run<IServerTaskService>(s => Should.ThrowAsync<ServerTaskOwnedByLiveProcessException>(() => s.StartExecutingAsync(id)));

        (await LoadAsync(id)).State.ShouldBe(TaskState.Executing);
    }

    // Both deliveries read the task as Pending/Paused; one CAS wins and every loser must be fenced - not routed to a failure path that would fail the winner.
    [Theory]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Paused)]
    public async Task ConcurrentStartExecuting_ExactlyOneWins_EveryLoserIsFenced_NothingIsWritten(string state)
    {
        for (var round = 0; round < 5; round++)
        {
            var id = await SeedAsync(state, $"deploy:claim-race-{state}-{round}");
            await Run<IDeploymentCheckpointService>(c => c.EnsureExistsAsync(id, 1)).ConfigureAwait(false);

            var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => TryStartExecutingAsync(id))).ConfigureAwait(false);

            outcomes.Count(o => o == null).ShouldBe(1, $"round {round}: exactly one delivery may start the pipeline");
            outcomes.Where(o => o != null).ShouldAllBe(o => o is ServerTaskOwnedByLiveProcessException, $"round {round}: every loser must be fenced, never see a transition error");

            var task = await LoadAsync(id);
            task.State.ShouldBe(TaskState.Executing);
            task.LeaseOwner.ShouldBe(Owner);
            (await Run<IDeploymentCheckpointService, object>(async c => await c.LoadAsync(id).ConfigureAwait(false))).ShouldNotBeNull("the checkpoint must survive the losers");
        }
    }

    // The runner on the loser's side, against the real database: the fenced delivery writes no completion, leaves the winner's state and lease, and starts no lease.
    [Fact]
    public async Task Runner_OnADuplicateDelivery_WritesNothing_AndLeavesTheWinnersTaskRunning()
    {
        var id = await SeedAsync(TaskState.Pending, "deploy:runner-dup");
        await Run<IServerTaskDataProvider>(p => p.TransitionStateAsync(id, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);   // the winner
        await Run<IDeploymentCheckpointService>(c => c.EnsureExistsAsync(id, 1)).ConfigureAwait(false);
        var before = await LoadAsync(id);
        var completion = new Moq.Mock<Squid.Core.Services.DeploymentExecution.Pipeline.IDeploymentCompletionHandler>();
        var lifecycle = new Moq.Mock<Squid.Core.Services.DeploymentExecution.Lifecycle.IDeploymentLifecycle>();

        await Run<IServerTaskService, IServerTaskLeaseKeeper, IServerTaskDataProvider>(async (service, keeper, provider) =>
        {
            var phase = new Squid.Core.Services.DeploymentExecution.Pipeline.Phases.LoadTaskPhase(service, keeper);
            var runner = new Squid.Core.Services.DeploymentExecution.Pipeline.DeploymentPipelineRunner([phase], lifecycle.Object, completion.Object, new Squid.Core.Services.DeploymentExecution.Script.TaskCancellationRegistry(), provider, Moq.Mock.Of<Squid.Core.Services.Jobs.ISquidBackgroundJobClient>());

            await runner.ProcessAsync(id, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        completion.VerifyNoOtherCalls();
        lifecycle.Verify(l => l.EmitAsync(Moq.It.IsAny<Squid.Core.Services.DeploymentExecution.Lifecycle.DeploymentLifecycleEvent>(), Moq.It.IsAny<CancellationToken>()), Moq.Times.Never);
        var after = await LoadAsync(id);
        after.State.ShouldBe(TaskState.Executing);
        after.LeaseOwner.ShouldBe(before.LeaseOwner);
        after.CompletedTime.ShouldBeNull();
        (await Run<IDeploymentCheckpointService, object>(async c => await c.LoadAsync(id).ConfigureAwait(false))).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Paused)]
    public async Task StartExecuting_FromPendingOrPaused_StampsThisProcess(string state)
    {
        var id = await SeedAsync(state, "deploy:fence-3");

        await Run<IServerTaskService, StartExecutingResult>(s => s.StartExecutingAsync(id));

        (await LoadAsync(id)).LeaseOwner.ShouldBe(Owner);
    }

    [Fact]
    public async Task TwoClaimantsOnAStaleExecutingRow_ExactlyOneWins()
    {
        for (var round = 0; round < 10; round++)
        {
            var id = await SeedAsync(TaskState.Executing, $"deploy:fence-race-{round}", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));

            var results = await Task.WhenAll(Claim(id, "pod-a:1:a"), Claim(id, "pod-b:2:b")).ConfigureAwait(false);

            results.Sum().ShouldBe(1, $"round {round}: one claimant takes the dead owner's lease, the other then sees a live owner");
        }
    }

    [Fact]
    public async Task StaleScan_ReturnsOnlyLeasedRunningTasksWhoseRenewalStopped()
    {
        var staleExecuting = await SeedAsync(TaskState.Executing, "deploy:s-1", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));
        var staleCancelling = await SeedAsync(TaskState.Cancelling, "deploy:s-2", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));
        await SeedAsync(TaskState.Executing, "deploy:s-3", leaseOwner: Owner, leaseAge: TimeSpan.FromMinutes(1));    // fresh
        await SeedAsync(TaskState.Paused, "deploy:s-4", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));  // needs no owner
        await SeedAsync(TaskState.Success, "deploy:s-5", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));

        var stale = await Run<IServerTaskLeaseDataProvider, List<StaleServerTask>>(p => p.GetStaleAsync(StaleAfter, 50));

        stale.Select(t => t.Id).OrderBy(i => i).ShouldBe(new[] { staleExecuting, staleCancelling }.OrderBy(i => i));
    }

    // A pre-lease row, or one claimed by an older server during a rolling upgrade, has no lease: nothing proves its owner died, so it is never touched.
    [Fact]
    public async Task NullLease_IsNeverReaped_HoweverOldTheTask()
    {
        var executing = await SeedAsync(TaskState.Executing, "deploy:n-1", startedAgo: TimeSpan.FromDays(30));
        var cancelling = await SeedAsync(TaskState.Cancelling, "deploy:n-2", startedAgo: TimeSpan.FromDays(30));

        var recovered = await Run<IServerTaskLeaseReaperService, int>(s => s.ReapStaleAsync());

        recovered.ShouldBe(0);
        (await LoadAsync(executing)).State.ShouldBe(TaskState.Executing);
        (await LoadAsync(cancelling)).State.ShouldBe(TaskState.Cancelling);
    }

    [Fact]
    public async Task FreshLease_IsNeverReaped()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:f-1", leaseOwner: Owner, leaseAge: TimeSpan.FromSeconds(30));

        (await Run<IServerTaskLeaseReaperService, int>(s => s.ReapStaleAsync())).ShouldBe(0);

        (await LoadAsync(id)).State.ShouldBe(TaskState.Executing);
    }

    [Fact]
    public async Task Reaper_PausesAnExecutingTask_KeepsItsCheckpoint_AndExplainsWhyInTheTaskLog()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:p-1", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));
        await Run<IDeploymentCheckpointService>(c => c.EnsureExistsAsync(id, 1)).ConfigureAwait(false);

        (await Run<IServerTaskLeaseReaperService, int>(s => s.ReapStaleAsync())).ShouldBe(1);

        var task = await LoadAsync(id);
        task.State.ShouldBe(TaskState.Paused, "a crash must not discard progress");
        task.LeaseOwner.ShouldBeNull();
        task.CompletedTime.ShouldBeNull();
        (await Run<IDeploymentCheckpointService, object>(async c => await c.LoadAsync(id).ConfigureAwait(false))).ShouldNotBeNull("the checkpoint is the resume point");

        var logs = await Run<IServerTaskLogDataProvider, List<Squid.Core.Persistence.Entities.Deployments.ServerTaskLog>>(p => p.GetLogsByTaskIdAsync(id));
        logs.ShouldContain(l => l.Source == "ServerTaskLeaseReaper" && l.MessageText.Contains(DeadOwner));
    }

    [Fact]
    public async Task Reaper_PausedTask_StillHoldsTheSlot_SoNoSecondDeploymentOverlapsItsPossiblyRunningScript()
    {
        var dead = await SeedAsync(TaskState.Executing, "deploy:slot-1", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));
        var next = await SeedAsync(TaskState.Pending, "deploy:slot-1");

        await Run<IServerTaskLeaseReaperService, int>(s => s.ReapStaleAsync()).ConfigureAwait(false);

        await Run<IServerTaskDataProvider>(async p => await Should.ThrowAsync<ConcurrencySlotOccupiedException>(() => p.TransitionStateAsync(next, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false)).ConfigureAwait(false);
        (await LoadAsync(dead)).State.ShouldBe(TaskState.Paused);
    }

    [Fact]
    public async Task Reaper_CompletesACancellingTask_AndFreesTheSlotForTheNextDeployment()
    {
        var dead = await SeedAsync(TaskState.Cancelling, "deploy:slot-2", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));
        var next = await SeedAsync(TaskState.Pending, "deploy:slot-2");
        await Run<IDeploymentCheckpointService>(c => c.EnsureExistsAsync(dead, 1)).ConfigureAwait(false);

        (await Run<IServerTaskLeaseReaperService, int>(s => s.ReapStaleAsync())).ShouldBe(1);

        var task = await LoadAsync(dead);
        task.State.ShouldBe(TaskState.Cancelled);
        task.CompletedTime.ShouldNotBeNull();
        (await Run<IDeploymentCheckpointService, object>(async c => await c.LoadAsync(dead).ConfigureAwait(false))).ShouldBeNull("a cancelled task's checkpoint is released");

        await Run<IServerTaskDataProvider>(p => p.TransitionStateAsync(next, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);
        (await LoadAsync(next)).State.ShouldBe(TaskState.Executing);
    }

    [Fact]
    public async Task Recover_RefusesATaskWhoseLeaseWasRenewedOrTakenOverSinceItWasRead()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:c-1", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));
        var candidate = new StaleServerTask(id, TaskState.Executing, DeadOwner);

        await Run<IServerTaskLeaseDataProvider, int>(p => p.AcquireAsync(id, "new-owner", StaleAfter)).ConfigureAwait(false);   // a takeover after the read

        (await Recover(candidate)).ShouldBe(0);
        (await LoadAsync(id)).State.ShouldBe(TaskState.Executing);
    }

    // Staleness alone must not authorise recovery: the candidate names the owner it saw, and a row held by anyone else is not the one that was read.
    [Fact]
    public async Task Recover_RefusesARowHeldByADifferentOwner_EvenWhenItIsStale()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:c-2", leaseOwner: "other-pod:2:def", leaseAge: TimeSpan.FromMinutes(10));

        (await Recover(new StaleServerTask(id, TaskState.Executing, DeadOwner))).ShouldBe(0);

        (await LoadAsync(id)).State.ShouldBe(TaskState.Executing);
    }

    // The genuine race: a live owner's renewal against the reaper's recovery of the same stale row, from independent scopes. Whichever
    // reaches the row first wins; the other must change nothing — never both, never neither.
    [Fact]
    public async Task RenewalRacingRecovery_ExactlyOneWins_EveryRound()
    {
        for (var round = 0; round < 25; round++)
        {
            var id = await SeedAsync(TaskState.Executing, $"deploy:race-{round}", leaseOwner: Owner, leaseAge: TimeSpan.FromMinutes(10));
            var candidate = new StaleServerTask(id, TaskState.Executing, Owner);

            var results = await Task.WhenAll(Renew(id, Owner), Recover(candidate)).ConfigureAwait(false);

            (results[0] + results[1]).ShouldBe(1, $"round {round}: renewal and recovery must not both win or both lose");

            var task = await LoadAsync(id);
            if (results[0] == 1) { task.State.ShouldBe(TaskState.Executing); task.LeaseRenewedAt.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1)); }
            else { task.State.ShouldBe(TaskState.Paused); task.LeaseOwner.ShouldBeNull(); }
        }
    }

    [Fact]
    public async Task TwoReapers_ExactlyOneRecoversTheTask()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:two-reapers", leaseOwner: DeadOwner, leaseAge: TimeSpan.FromMinutes(10));
        var candidate = new StaleServerTask(id, TaskState.Executing, DeadOwner);

        var results = await Task.WhenAll(Recover(candidate), Recover(candidate)).ConfigureAwait(false);

        results.Sum().ShouldBe(1);
    }

    private Task<Exception> TryStartExecutingAsync(int id) => Run<IServerTaskService, Exception>(async s =>
    {
        try
        {
            await s.StartExecutingAsync(id).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    });

    // ===== A lease belongs to one RUN, not to the process =====

    // Out of a running state a run's write applies only while the row still carries that run's lease; another run's row is refused and left untouched.
    [Theory]
    [InlineData(TaskState.Executing, TaskState.Paused)]
    [InlineData(TaskState.Executing, TaskState.Success)]
    [InlineData(TaskState.Executing, TaskState.Failed)]
    [InlineData(TaskState.Cancelling, TaskState.Cancelled)]
    [InlineData(TaskState.Cancelling, TaskState.Failed)]
    public async Task ARunsWrite_OutOfARunningState_IsRefusedWhenAnotherRunHoldsTheLease(string from, string to)
    {
        const string runA = "host:1:proc/run-a";
        const string runB = "host:1:proc/run-b";
        var id = await SeedAsync(from, $"deploy:run-{from}-{to}", leaseOwner: runB, leaseAge: TimeSpan.FromSeconds(5));

        var ex = await Run<IServerTaskDataProvider, ServerTaskLeaseNotHeldException>(p => Should.ThrowAsync<ServerTaskLeaseNotHeldException>(() => p.TransitionStateAsync(id, from, to, runA)));

        ex.ExpectedOwner.ShouldBe(runA);
        ex.CurrentOwner.ShouldBe(runB);
        var untouched = await LoadAsync(id);
        untouched.State.ShouldBe(from);
        untouched.LeaseOwner.ShouldBe(runB);
        untouched.CompletedTime.ShouldBeNull();

        await Run<IServerTaskDataProvider>(p => p.TransitionStateAsync(id, from, to, runB)).ConfigureAwait(false);

        (await LoadAsync(id)).State.ShouldBe(to, "the run that holds the lease writes freely");
    }

    // The ownership condition is for running states only: the claim (from Pending/Paused) and every non-run caller carry no owner and are unconditional.
    [Fact]
    public async Task AWriteWithoutAnOwner_IsUnconditional_AsEveryNonRunCallerIs()
    {
        var id = await SeedAsync(TaskState.Executing, "deploy:run-null", leaseOwner: "host:1:proc/run-b", leaseAge: TimeSpan.FromSeconds(5));

        await Run<IServerTaskDataProvider>(p => p.TransitionStateAsync(id, TaskState.Executing, TaskState.Cancelling)).ConfigureAwait(false);

        (await LoadAsync(id)).State.ShouldBe(TaskState.Cancelling);
    }

    // Two runs of one task in one process (the resume starts while the paused run is still unwinding): each claim stamps its own run, so the older run's
    // late pause is refused instead of pausing the newer run's task, and only the newer run's renewals keep the lease.
    [Fact]
    public async Task ASecondRunInTheSameProcess_OwnsTheLease_TheFirstRunsLateWritesAreRefused()
    {
        var runA = ServerTaskLeaseOwner.NewRun();
        var runB = ServerTaskLeaseOwner.NewRun();
        var id = await SeedAsync(TaskState.Pending, "deploy:two-runs");

        await Run<IServerTaskService, StartExecutingResult>(s => s.StartExecutingAsync(id, runA)).ConfigureAwait(false);
        (await LoadAsync(id)).LeaseOwner.ShouldBe(runA);

        await Run<IServerTaskService>(s => s.TransitionStateAsync(id, TaskState.Executing, TaskState.Paused, runA)).ConfigureAwait(false);   // run A pauses itself
        await Run<IServerTaskService, StartExecutingResult>(s => s.StartExecutingAsync(id, runB)).ConfigureAwait(false);                       // the resume claims it

        var row = await LoadAsync(id);
        row.State.ShouldBe(TaskState.Executing);
        row.LeaseOwner.ShouldBe(runB);

        await Run<IServerTaskService>(s => Should.ThrowAsync<ServerTaskLeaseNotHeldException>(() => s.TransitionStateAsync(id, TaskState.Executing, TaskState.Paused, runA))).ConfigureAwait(false);
        (await Renew(id, runA)).ShouldBe(0, "run A must not keep the lease alive for run B's row");
        (await Renew(id, runB)).ShouldBe(1);
        (await LoadAsync(id)).State.ShouldBe(TaskState.Executing);
    }

    // The real keeper against the real database: a run that releases its lease (it pauses itself) and then sees its renewal find nothing is not "lost".
    [Fact]
    public async Task Keeper_ARunThatPausesItself_AfterRelease_IsNotLost_ButOneThatIsTakenOverIs()
    {
        var runA = ServerTaskLeaseOwner.NewRun();
        var paused = await SeedAsync(TaskState.Executing, "deploy:keeper-release", leaseOwner: runA, leaseAge: TimeSpan.Zero);
        var takenOver = await SeedAsync(TaskState.Executing, "deploy:keeper-takeover", leaseOwner: runA, leaseAge: TimeSpan.Zero);

        await Run<ILifetimeScope>(async scope =>
        {
            var keeper = new ServerTaskLeaseKeeper(scope, TimeProvider.System, new ServerTaskLeaseOptions { RenewInterval = TimeSpan.FromMilliseconds(100), Stale = TimeSpan.FromMinutes(5) });
            await using var releasing = keeper.Start(paused, runA);
            await using var losing = keeper.Start(takenOver, runA);
            await Task.Delay(300).ConfigureAwait(false);

            releasing.Release();
            await Run<IServerTaskService>(s => s.TransitionStateAsync(paused, TaskState.Executing, TaskState.Paused, runA)).ConfigureAwait(false);
            await TakeOverAsync(takenOver, "other-run").ConfigureAwait(false);
            var noticed = await NoticedLossWithinAsync(losing, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            noticed.ShouldBeTrue("the keeper never reported the lease another run took (watched losing.LostToken for 30s); check that RenewAsync updates no row once LeaseOwner changed");
            losing.IsLost.ShouldBeTrue("another run took the lease");

            // Give a keeper that wrongly kept renewing after Release a few intervals to find the cleared lease (this wait can only expose a bug, never cause one).
            await Task.Delay(500).ConfigureAwait(false);

            releasing.IsLost.ShouldBeFalse("the run cleared its own lease");
        }).ConfigureAwait(false);
    }

    // The keeper renews the lease every interval, and a renewal that lands while the steal waits for the row lock gives the row a renewal time later than
    // the steal's own now(): that steal then updates nothing (correctly: a lease just renewed is not stale). Retry until it lands instead of assuming it did.
    private async Task TakeOverAsync(int id, string owner)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (await Run<IServerTaskLeaseDataProvider, int>(p => p.AcquireAsync(id, owner, TimeSpan.Zero)).ConfigureAwait(false) == 0)
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Could not take over the lease of task {id} within 10s: every attempt met a renewal that had just landed");

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    // A beat that takes longer than the renew interval is abandoned and retried, so on a busy machine the renewal that finds the stolen lease may come
    // several intervals later: wait for the loss itself (bounded) rather than a fixed time.
    private static async Task<bool> NoticedLossWithinAsync(IServerTaskLease lease, TimeSpan limit)
    {
        try
        {
            await Task.Delay(limit, lease.LostToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private Task<int> Claim(int id, string owner) => Run<IServerTaskLeaseDataProvider, int>(p => p.AcquireAsync(id, owner, StaleAfter));

    private Task<int> Renew(int id, string owner) => Run<IServerTaskLeaseDataProvider, int>(p => p.RenewAsync(id, owner));

    private Task<int> Recover(StaleServerTask candidate) => Run<IServerTaskLeaseDataProvider, int>(p => p.RecoverAsync(candidate, TaskState.Paused, StaleAfter));

    private Task<ServerTaskEntity> LoadAsync(int id) => Run<IRepository, ServerTaskEntity>(r => r.QueryNoTracking<ServerTaskEntity>(t => t.Id == id).FirstAsync());

    private async Task<int> SeedAsync(string state, string tag, string leaseOwner = null, TimeSpan? leaseAge = null, TimeSpan? startedAgo = null, TimeSpan? modifiedAgo = null)
    {
        var task = new ServerTaskEntity
        {
            Name = "DeploymentTask",
            Description = "lease integration",
            QueueTime = DateTimeOffset.UtcNow.AddDays(-31),
            StartTime = DateTimeOffset.UtcNow - (startedAgo ?? TimeSpan.FromMinutes(20)),
            State = state,
            ServerNodeId = Guid.Empty,
            JSON = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ConcurrencyTag = tag,
            SpaceId = 1,
            LastModifiedDate = DateTimeOffset.UtcNow,
            ErrorMessage = string.Empty,
            BusinessProcessState = string.Empty,
            ServerTaskType = string.Empty,
            JobId = string.Empty,
            LeaseOwner = leaseOwner,
            LeaseRenewedAt = leaseOwner == null ? null : DateTimeOffset.UtcNow - (leaseAge ?? TimeSpan.Zero)
        };

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(task).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // The context stamps LastModifiedDate on every tracked save, so the age of the row is set afterwards, the way the transitions (bulk updates) leave it.
            if (modifiedAgo != null)
                await repository.ExecuteUpdateAsync<ServerTaskEntity>(t => t.Id == task.Id, s => s.SetProperty(t => t.LastModifiedDate, DateTimeOffset.UtcNow - modifiedAgo.Value), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return task.Id;
    }
}
