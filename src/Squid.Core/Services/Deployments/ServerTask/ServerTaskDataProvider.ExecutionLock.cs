using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Squid.Core.Services.Deployments.ServerTask;

public partial class ServerTaskDataProvider
{
    private const int TriggerExecutionLockNamespace = 0x53515445;

    public Task<bool> IsCancellationRequestedAsync(int taskId, CancellationToken ct) =>
        _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(task =>
            task.Id == taskId && (task.State == TaskState.Cancelling || task.State == TaskState.Cancelled)).AnyAsync(ct);

    public async Task<bool> TryClaimUnleasedCancellationAsync(int taskId, string leaseOwner, CancellationToken ct)
    {
        var claimed = await _repository.ExecuteUpdateAsync<Persistence.Entities.Deployments.ServerTask>(
            t => t.Id == taskId && t.IsTriggerTask && t.State == TaskState.Cancelling && t.LeaseOwner == null,
            s => s.SetProperty(t => t.LeaseOwner, leaseOwner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        return claimed == 1;
    }

    // Pin the scoped connection for this delivery. Pipeline transactions may still commit independently.
    // PostgreSQL releases a session lock when the worker's database connection disappears.
    public async Task RunWithTriggerTaskLockAsync(int taskId, bool waitForOwner, Func<Task> run, CancellationToken ct)
    {
        var database = _repository.Database;
        await database.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            await RunOnLockedConnectionAsync(database, taskId, waitForOwner, run, ct).ConfigureAwait(false);
        }
        finally
        {
            await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunOnLockedConnectionAsync(DatabaseFacade database, int taskId, bool waitForOwner, Func<Task> run, CancellationToken ct)
    {
        // If cancellation interrupts the acquire response, PostgreSQL may already hold the lock.
        var releaseLock = true;

        try
        {
            if (waitForOwner)
                await database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({TriggerExecutionLockNamespace}, {taskId})", ct).ConfigureAwait(false);
            else
                releaseLock = await database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({TriggerExecutionLockNamespace}, {taskId}) AS \"Value\"")
                    .SingleAsync(ct).ConfigureAwait(false);

            if (releaseLock) await run().ConfigureAwait(false);
        }
        finally
        {
            if (releaseLock)
                await database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({TriggerExecutionLockNamespace}, {taskId})", CancellationToken.None).ConfigureAwait(false);
        }
    }
}
