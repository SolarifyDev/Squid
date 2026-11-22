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

    [Theory]
    [InlineData(TaskState.Executing, 1)]
    [InlineData(TaskState.Cancelling, 1)]
    [InlineData(TaskState.Paused, 0)]
    [InlineData(TaskState.Pending, 0)]
    public async Task Acquire_TakesOverARunningTask_NeverAnIdleOne(string state, int expectedRows)
    {
        var id = await SeedAsync(state, "deploy:a-1", leaseOwner: state == TaskState.Executing ? DeadOwner : null, leaseAge: TimeSpan.FromMinutes(30));

        var rows = await Run<IServerTaskLeaseDataProvider, int>(p => p.AcquireAsync(id, Owner));

        rows.ShouldBe(expectedRows);
        if (expectedRows == 1) (await LoadAsync(id)).LeaseOwner.ShouldBe(Owner);
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

        await Run<IServerTaskLeaseDataProvider, int>(p => p.AcquireAsync(id, "new-owner")).ConfigureAwait(false);   // a takeover after the read

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

    private Task<int> Renew(int id, string owner) => Run<IServerTaskLeaseDataProvider, int>(p => p.RenewAsync(id, owner));

    private Task<int> Recover(StaleServerTask candidate) => Run<IServerTaskLeaseDataProvider, int>(p => p.RecoverAsync(candidate, TaskState.Paused, StaleAfter));

    private Task<ServerTaskEntity> LoadAsync(int id) => Run<IRepository, ServerTaskEntity>(r => r.QueryNoTracking<ServerTaskEntity>(t => t.Id == id).FirstAsync());

    private async Task<int> SeedAsync(string state, string tag, string leaseOwner = null, TimeSpan? leaseAge = null, TimeSpan? startedAgo = null)
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
        }).ConfigureAwait(false);

        return task.Id;
    }
}
