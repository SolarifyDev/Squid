using System.Linq.Expressions;
using Squid.Core.Persistence.Db;
using Squid.Message.Models.Deployments.ServerTask;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using ServerTaskLogEntity = Squid.Core.Persistence.Entities.Deployments.ServerTaskLog;

namespace Squid.Core.Services.Deployments.ServerTask.Lease;

public interface IServerTaskLeaseDataProvider : IScopedDependency
{
    /// <summary>Takes the lease of a task that is running (Executing/Cancelling) whoever held it; returns 0 when the task is in any other state.</summary>
    Task<int> AcquireAsync(int taskId, string owner, CancellationToken ct = default);

    /// <summary>Renews the lease only while <paramref name="owner"/> still holds it on a running task; returns 0 when it does not.</summary>
    Task<int> RenewAsync(int taskId, string owner, CancellationToken ct = default);

    Task<List<StaleServerTask>> GetStaleAsync(TimeSpan staleAfter, int take, CancellationToken ct = default);

    /// <summary>Moves a stale task to <paramref name="toState"/> only if it is still in that state, held by the same owner and as stale as when it was read; returns 0 when it lost the race.</summary>
    Task<int> RecoverAsync(StaleServerTask task, string toState, TimeSpan staleAfter, CancellationToken ct = default);

    Task<long> GetMaxLogSequenceAsync(int taskId, CancellationToken ct = default);
}

public class ServerTaskLeaseDataProvider(IRepository repository) : IServerTaskLeaseDataProvider
{
    // DateTimeOffset.UtcNow inside a query is translated to the database's now(), never sent as this server's time, so every pod and the reaper share one clock.
    public Task<int> AcquireAsync(int taskId, string owner, CancellationToken ct = default)
        => repository.ExecuteUpdateAsync<ServerTaskEntity>(
            t => t.Id == taskId && (t.State == TaskState.Executing || t.State == TaskState.Cancelling),
            s => s.SetProperty(t => t.LeaseOwner, owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow),
            ct);

    public Task<int> RenewAsync(int taskId, string owner, CancellationToken ct = default)
        => repository.ExecuteUpdateAsync<ServerTaskEntity>(
            t => t.Id == taskId && t.LeaseOwner == owner && (t.State == TaskState.Executing || t.State == TaskState.Cancelling),
            s => s.SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow),
            ct);

    public async Task<List<StaleServerTask>> GetStaleAsync(TimeSpan staleAfter, int take, CancellationToken ct = default)
    {
        var stale = await repository.QueryNoTracking<ServerTaskEntity>(IsStale(staleAfter))
            .OrderBy(t => t.LeaseRenewedAt)
            .Take(take)
            .Select(t => new { t.Id, t.State, t.LeaseOwner })
            .ToListAsync(ct).ConfigureAwait(false);

        return stale.Select(t => new StaleServerTask(t.Id, t.State, t.LeaseOwner)).ToList();
    }

    public Task<int> RecoverAsync(StaleServerTask task, string toState, TimeSpan staleAfter, CancellationToken ct = default)
    {
        TaskState.EnsureValidTransition(task.State, toState);

        var now = DateTimeOffset.UtcNow;
        var dataVersion = Guid.NewGuid().ToByteArray();
        var unchanged = StillStale(task, staleAfter);

        return TaskState.IsTerminal(toState) ? RecoverToTerminalAsync(unchanged, toState, now, dataVersion, ct) : RecoverToActiveAsync(unchanged, toState, now, dataVersion, ct);
    }

    public async Task<long> GetMaxLogSequenceAsync(int taskId, CancellationToken ct = default)
    {
        var max = await repository.QueryNoTracking<ServerTaskLogEntity>(l => l.ServerTaskId == taskId).MaxAsync(l => (long?)l.SequenceNumber, ct).ConfigureAwait(false);

        return max ?? 0;
    }

    // The lease is cleared: a recovered row has no live owner, and its next claim stamps a new one.
    private Task<int> RecoverToActiveAsync(Expression<Func<ServerTaskEntity, bool>> unchanged, string toState, DateTimeOffset now, byte[] dataVersion, CancellationToken ct)
        => repository.ExecuteUpdateAsync<ServerTaskEntity>(
            unchanged,
            s => s.SetProperty(t => t.State, toState).SetProperty(t => t.DataVersion, dataVersion).SetProperty(t => t.LastModifiedDate, now).SetProperty(t => t.LeaseOwner, (string)null).SetProperty(t => t.LeaseRenewedAt, (DateTimeOffset?)null),
            ct);

    private Task<int> RecoverToTerminalAsync(Expression<Func<ServerTaskEntity, bool>> unchanged, string toState, DateTimeOffset now, byte[] dataVersion, CancellationToken ct)
        => repository.ExecuteUpdateAsync<ServerTaskEntity>(
            unchanged,
            s => s.SetProperty(t => t.State, toState).SetProperty(t => t.DataVersion, dataVersion).SetProperty(t => t.LastModifiedDate, now).SetProperty(t => t.CompletedTime, now).SetProperty(t => t.LeaseOwner, (string)null).SetProperty(t => t.LeaseRenewedAt, (DateTimeOffset?)null),
            ct);

    // A NULL lease never matches (NULL <= x is not true): a row nobody leased has no proof its owner died and is left alone.
    private static Expression<Func<ServerTaskEntity, bool>> IsStale(TimeSpan staleAfter)
        => t => (t.State == TaskState.Executing || t.State == TaskState.Cancelling) && t.LeaseRenewedAt != null && t.LeaseRenewedAt <= DateTimeOffset.UtcNow - staleAfter;

    // The candidate as it was read: the same state, the same owner, still silent for the threshold. A renewal or a takeover in between makes this false.
    private static Expression<Func<ServerTaskEntity, bool>> StillStale(StaleServerTask task, TimeSpan staleAfter)
    {
        var (id, state, owner) = (task.Id, task.State, task.LeaseOwner);

        return t => t.Id == id && t.State == state && t.LeaseOwner == owner && t.LeaseRenewedAt != null && t.LeaseRenewedAt <= DateTimeOffset.UtcNow - staleAfter;
    }
}
