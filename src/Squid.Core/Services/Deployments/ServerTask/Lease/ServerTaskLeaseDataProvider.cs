using System.Linq.Expressions;
using Squid.Core.Persistence.Db;
using Squid.Message.Models.Deployments.ServerTask;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using ServerTaskLogEntity = Squid.Core.Persistence.Entities.Deployments.ServerTaskLog;

namespace Squid.Core.Services.Deployments.ServerTask.Lease;

public interface IServerTaskLeaseDataProvider : IScopedDependency
{
    /// <summary>
    /// Takes the lease of a running task (Executing/Cancelling) only when <paramref name="owner"/> already holds it, nobody does (NULL), or the
    /// holder's lease is stale; returns 0 when a live foreign owner holds it or the task is in any other state.
    /// </summary>
    Task<int> AcquireAsync(int taskId, string owner, TimeSpan staleAfter, CancellationToken ct = default);

    /// <summary>
    /// The claim-time fence: takes the lease of a running task only when nobody holds it (NULL) or the holder's lease is stale. Unlike
    /// <see cref="AcquireAsync"/> the caller's own lease does NOT qualify: a second delivery inside the same process is as much a duplicate as one
    /// on another pod, and must not start a second pipeline beside the live one.
    /// </summary>
    Task<int> ClaimRunningAsync(int taskId, string owner, TimeSpan staleAfter, CancellationToken ct = default);

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
    public Task<int> AcquireAsync(int taskId, string owner, TimeSpan staleAfter, CancellationToken ct = default)
        => repository.ExecuteUpdateAsync<ServerTaskEntity>(
            Takeable(taskId, owner, staleAfter, includeOwn: true),
            s => s.SetProperty(t => t.LeaseOwner, owner).SetProperty(t => t.LeaseRenewedAt, t => DateTimeOffset.UtcNow),
            ct);

    public Task<int> ClaimRunningAsync(int taskId, string owner, TimeSpan staleAfter, CancellationToken ct = default)
        => repository.ExecuteUpdateAsync<ServerTaskEntity>(
            Takeable(taskId, owner, staleAfter, includeOwn: false),
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

    // The one definition of "the owner stopped renewing", on the database clock. A NULL lease never matches (NULL <= x is not true): a row
    // nobody leased has no proof its owner died. The scan, the recovery CAS and the takeover rule are all built from this.
    private static Expression<Func<ServerTaskEntity, bool>> StaleLease(TimeSpan staleAfter)
        => t => t.LeaseRenewedAt != null && t.LeaseRenewedAt <= DateTimeOffset.UtcNow - staleAfter;

    private static Expression<Func<ServerTaskEntity, bool>> Running()
        => t => t.State == TaskState.Executing || t.State == TaskState.Cancelling;

    private static Expression<Func<ServerTaskEntity, bool>> IsStale(TimeSpan staleAfter) => Both(Running(), StaleLease(staleAfter));

    // The candidate as it was read: the same state, the same owner, still silent for the threshold. A renewal or a takeover in between makes this false.
    private static Expression<Func<ServerTaskEntity, bool>> StillStale(StaleServerTask task, TimeSpan staleAfter)
    {
        var (id, state, owner) = (task.Id, task.State, task.LeaseOwner);

        return Both(t => t.Id == id && t.State == state && t.LeaseOwner == owner, StaleLease(staleAfter));
    }

    // A running task nobody holds, or whose holder went silent, or (when includeOwn) whose lease this owner already holds. A live foreign owner never matches.
    internal static Expression<Func<ServerTaskEntity, bool>> Takeable(int taskId, string owner, TimeSpan staleAfter, bool includeOwn)
    {
        Expression<Func<ServerTaskEntity, bool>> unheld = t => t.LeaseRenewedAt == null;
        Expression<Func<ServerTaskEntity, bool>> mine = t => t.LeaseOwner == owner;
        var unheldOrMine = includeOwn ? Either(unheld, mine) : unheld;

        return Both(Both(t => t.Id == taskId, Running()), Either(unheldOrMine, StaleLease(staleAfter)));
    }

    private static Expression<Func<ServerTaskEntity, bool>> Both(Expression<Func<ServerTaskEntity, bool>> left, Expression<Func<ServerTaskEntity, bool>> right) => Join(left, right, Expression.AndAlso);

    private static Expression<Func<ServerTaskEntity, bool>> Either(Expression<Func<ServerTaskEntity, bool>> left, Expression<Func<ServerTaskEntity, bool>> right) => Join(left, right, Expression.OrElse);

    private static Expression<Func<ServerTaskEntity, bool>> Join(Expression<Func<ServerTaskEntity, bool>> left, Expression<Func<ServerTaskEntity, bool>> right, Func<Expression, Expression, BinaryExpression> combine)
    {
        var parameter = left.Parameters[0];
        var rebound = new ParameterRebinder(right.Parameters[0], parameter).Visit(right.Body);

        return Expression.Lambda<Func<ServerTaskEntity, bool>>(combine(left.Body, rebound), parameter);
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
