using System.Collections.Concurrent;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Checkpoints;

/// <summary>
/// Durable record of scripts dispatched to a Halibut agent but not yet observed
/// to completion, persisted on the deployment checkpoint's
/// <see cref="DeploymentExecutionCheckpoint.InFlightScriptsJson"/> column so a
/// deployment resumed after a server crash can re-attach to a still-running
/// script (probe the agent with the same ScriptTicket) instead of launching a
/// duplicate.
///
/// <para><b>Concurrency</b>: a parallel batch dispatches to several targets at
/// once, each recording its ticket. A single Hangfire worker owns a given
/// deployment task, so all writes for one <c>serverTaskId</c> happen in one
/// process — an in-process per-task lock serialises the read-modify-write of
/// the shared JSON column. (Cross-process contention can't occur: task
/// ownership is exclusive.)</para>
///
/// <para><b>Fail-safe</b>: if the checkpoint row does not exist yet, recording
/// is silently skipped — the worst case is that resume re-dispatches (today's
/// behaviour) rather than re-attaching. The executor creates the row before any
/// dispatch, so this is only a defensive guard.</para>
/// </summary>
public interface IInFlightScriptStore : IScopedDependency
{
    Task RecordDispatchedAsync(int serverTaskId, int machineId, string scriptTicket, CancellationToken cancellationToken = default);

    Task ClearAsync(int serverTaskId, int machineId, CancellationToken cancellationToken = default);

    Task<string?> TryGetTicketAsync(int serverTaskId, int machineId, CancellationToken cancellationToken = default);
}

public sealed class InFlightScriptStore(IRepository repository) : IInFlightScriptStore
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> TaskLocks = new();

    public Task RecordDispatchedAsync(int serverTaskId, int machineId, string scriptTicket, CancellationToken cancellationToken = default)
        => MutateAsync(serverTaskId, current => InFlightScriptMap.Add(current, machineId, scriptTicket), cancellationToken);

    public Task ClearAsync(int serverTaskId, int machineId, CancellationToken cancellationToken = default)
        => MutateAsync(serverTaskId, current => InFlightScriptMap.Remove(current, machineId), cancellationToken);

    public async Task<string?> TryGetTicketAsync(int serverTaskId, int machineId, CancellationToken cancellationToken = default)
    {
        var row = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == serverTaskId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return row == null ? null : InFlightScriptMap.TryGet(row.InFlightScriptsJson ?? "{}", machineId);
    }

    private async Task MutateAsync(int serverTaskId, Func<string, string> mutate, CancellationToken cancellationToken)
    {
        var gate = TaskLocks.GetOrAdd(serverTaskId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var row = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == serverTaskId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (row == null)
            {
                Log.Debug("[Deploy] No checkpoint row for task {ServerTaskId} while recording in-flight script — skipping (resume will re-dispatch).", serverTaskId);
                return;
            }

            var updated = mutate(row.InFlightScriptsJson ?? "{}");

            await repository.ExecuteUpdateAsync<DeploymentExecutionCheckpoint>(
                c => c.ServerTaskId == serverTaskId,
                s => s.SetProperty(c => c.InFlightScriptsJson, updated),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
