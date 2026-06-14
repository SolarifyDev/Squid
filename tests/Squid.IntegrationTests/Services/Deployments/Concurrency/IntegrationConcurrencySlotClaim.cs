using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.Deployments.Concurrency;

/// <summary>
/// Real-Postgres coverage of the cross-process atomic concurrency-slot guarantee
/// (<c>ux_server_task_active_per_tag</c>): at most ONE ACTIVE task (Executing, Paused, or
/// Cancelling) per ConcurrencyTag. The runner's free-slot poll is only a fast path — THIS unique
/// partial index is the load-bearing guard that makes two pods physically unable to both run a
/// deployment to the same environment. A second Pending/Paused→Executing transition while the
/// tag already has an active task is rejected (23505) and surfaced as
/// <see cref="ConcurrencySlotOccupiedException"/>; a Paused/Cancelling holder still blocks;
/// different tags and untagged tasks are unconstrained; the slot frees when the holder reaches a
/// terminal state. The final test fires two claims concurrently from independent scopes and
/// asserts exactly one wins — the genuine multi-pod race.
/// </summary>
public class IntegrationConcurrencySlotClaim : TestBase
{
    public IntegrationConcurrencySlotClaim()
        : base("ConcurrencySlotClaim", "squid_it_concurrency_slot_claim")
    {
    }

    [Fact]
    public async Task SecondExecutingWithSameTag_IsRejectedAsSlotOccupied()
    {
        var (occupantId, claimantId) = await SeedOccupantAndClaimantAsync("deploy:env-1");

        await Run<IServerTaskDataProvider>(async provider =>
        {
            var ex = await Should.ThrowAsync<ConcurrencySlotOccupiedException>(() =>
                provider.TransitionStateAsync(claimantId, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);

            ex.ConcurrencyTag.ShouldBe("deploy:env-1");
        }).ConfigureAwait(false);

        await AssertStateAsync(claimantId, TaskState.Pending, "the loser must stay Pending — the failed claim made no state change");
        await AssertStateAsync(occupantId, TaskState.Executing, "the occupant keeps the slot");
    }

    [Theory]
    [InlineData(TaskState.Paused)]
    [InlineData(TaskState.Cancelling)]
    public async Task ActiveButNotExecutingHolder_StillOccupiesSlot_SecondClaimRejected(string holderState)
    {
        // A Paused (transient-pause/timeout) or Cancelling holder still owns the slot because its
        // in-flight agent script may be running — a same-tag deployment must NOT start over it.
        var (occupantId, claimantId) = await SeedOccupantAndClaimantAsync("deploy:env-1", occupantState: holderState);

        await Run<IServerTaskDataProvider>(async provider =>
        {
            await Should.ThrowAsync<ConcurrencySlotOccupiedException>(() =>
                provider.TransitionStateAsync(claimantId, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await AssertStateAsync(claimantId, TaskState.Pending, $"a {holderState} holder still occupies the slot, so the claimant stays Pending");
        await AssertStateAsync(occupantId, holderState, "the active (non-Executing) holder is unchanged");
    }

    [Fact]
    public async Task DifferentTags_BothExecuting_Allowed()
    {
        var (_, claimantId) = await SeedOccupantAndClaimantAsync("deploy:env-1", claimantTag: "deploy:env-2");

        await Run<IServerTaskDataProvider>(provider =>
            provider.TransitionStateAsync(claimantId, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);

        await AssertStateAsync(claimantId, TaskState.Executing, "a different tag is a different slot — no conflict");
    }

    [Fact]
    public async Task UntaggedTasks_MultipleExecuting_Allowed()
    {
        // The unique index is filtered to "concurrency_tag IS NOT NULL", so untagged tasks
        // (no per-environment serialization) are never constrained.
        var (_, claimantId) = await SeedOccupantAndClaimantAsync(occupantTag: null, claimantTag: null);

        await Run<IServerTaskDataProvider>(provider =>
            provider.TransitionStateAsync(claimantId, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);

        await AssertStateAsync(claimantId, TaskState.Executing, "untagged tasks are not slot-constrained");
    }

    [Fact]
    public async Task SlotFreesWhenHolderLeavesExecuting_ThenClaimSucceeds()
    {
        var (occupantId, claimantId) = await SeedOccupantAndClaimantAsync("deploy:env-1");

        await Run<IServerTaskDataProvider>(provider =>
            provider.TransitionStateAsync(occupantId, TaskState.Executing, TaskState.Success)).ConfigureAwait(false);

        await Run<IServerTaskDataProvider>(provider =>
            provider.TransitionStateAsync(claimantId, TaskState.Pending, TaskState.Executing)).ConfigureAwait(false);

        await AssertStateAsync(claimantId, TaskState.Executing, "the slot frees once the holder leaves Executing (Success), so the next claim wins");
    }

    [Fact]
    public async Task TwoConcurrentClaims_SameTag_ExactlyOneWins()
    {
        // The genuine multi-pod race: two Pending tasks, same tag, two independent scopes
        // (DbContexts) firing the →Executing claim at once. The unique index serializes them at
        // commit, so exactly one commits and the other gets 23505 → ConcurrencySlotOccupiedException.
        var (aId, bId) = await SeedTwoPendingAsync("deploy:env-9");

        var aOutcome = ClaimAsync(aId);
        var bOutcome = ClaimAsync(bId);

        var results = await Task.WhenAll(aOutcome, bOutcome).ConfigureAwait(false);

        results.Count(won => won).ShouldBe(1, customMessage: "exactly one concurrent claim may win the slot");
        results.Count(won => !won).ShouldBe(1, customMessage: "the other concurrent claim must be rejected as slot-occupied");

        var executing = await CountExecutingAsync("deploy:env-9");
        executing.ShouldBe(1, customMessage: "the database must hold exactly one Executing row for the tag");
    }

    private Task<bool> ClaimAsync(int taskId) => Run<IServerTaskDataProvider, bool>(async provider =>
    {
        try
        {
            await provider.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing).ConfigureAwait(false);
            return true;
        }
        catch (ConcurrencySlotOccupiedException)
        {
            return false;
        }
    });

    private async Task<(int occupantId, int claimantId)> SeedOccupantAndClaimantAsync(string occupantTag, string claimantTag = null, string occupantState = TaskState.Executing)
    {
        var occupantId = 0;
        var claimantId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var occupant = NewTask(occupantState, occupantTag);
            var claimant = NewTask(TaskState.Pending, claimantTag ?? occupantTag);

            await repository.InsertAsync(occupant).ConfigureAwait(false);
            await repository.InsertAsync(claimant).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            occupantId = occupant.Id;
            claimantId = claimant.Id;
        }).ConfigureAwait(false);

        return (occupantId, claimantId);
    }

    private async Task<(int aId, int bId)> SeedTwoPendingAsync(string tag)
    {
        var aId = 0;
        var bId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var a = NewTask(TaskState.Pending, tag);
            var b = NewTask(TaskState.Pending, tag);

            await repository.InsertAsync(a).ConfigureAwait(false);
            await repository.InsertAsync(b).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            aId = a.Id;
            bId = b.Id;
        }).ConfigureAwait(false);

        return (aId, bId);
    }

    private Task AssertStateAsync(int taskId, string expected, string because) => Run<IRepository>(async repository =>
    {
        var task = await repository.QueryNoTracking<ServerTaskEntity>(t => t.Id == taskId).FirstOrDefaultAsync().ConfigureAwait(false);
        task.State.ShouldBe(expected, customMessage: because);
    });

    private Task<int> CountExecutingAsync(string tag) => Run<IRepository, int>(repository =>
        repository.QueryNoTracking<ServerTaskEntity>(t => t.ConcurrencyTag == tag && t.State == TaskState.Executing).CountAsync());

    private static ServerTaskEntity NewTask(string state, string concurrencyTag) => new()
    {
        Name = "DeploymentTask",
        Description = "concurrency-slot integration",
        QueueTime = DateTimeOffset.UtcNow.AddMinutes(-1),
        StartTime = string.Equals(state, TaskState.Executing, StringComparison.Ordinal) ? DateTimeOffset.UtcNow : null,
        State = state,
        HasWarningsOrErrors = false,
        ServerNodeId = Guid.Empty,
        ProjectId = 0,
        EnvironmentId = 0,
        DurationSeconds = 0,
        BatchId = 0,
        JSON = "{}",
        DataVersion = Guid.NewGuid().ToByteArray(),
        SpaceId = 1,
        LastModifiedDate = DateTimeOffset.UtcNow,
        ConcurrencyTag = concurrencyTag,
        ErrorMessage = string.Empty,
        BusinessProcessState = string.Empty,
        ServerTaskType = string.Empty,
        StateOrder = 0,
        Weight = 0,
        JobId = string.Empty
    };
}
