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
/// once, each recording its ticket AND probing for a re-attach ticket — all on
/// the one scoped <see cref="IRepository"/>/DbContext the Hangfire worker owns
/// for that task. Every DbContext access (the read-only <see cref="TryGetTicketAsync"/>
/// probe and the read-modify-write of the shared JSON column) takes a per-task
/// lock stripe (keyed by <c>serverTaskId</c>), so two parallel targets can never
/// drive two concurrent operations onto the shared context (EF would throw "a
/// second operation was started on this context instance"). Striping is bounded
/// (no per-task growth over the server's lifetime); two tasks hashing to the same
/// stripe serialise harmlessly, and a given task always maps to the same stripe
/// so all of its own access serialises. (Cross-process contention can't occur:
/// task ownership is exclusive.)</para>
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
    // Bounded lock stripes — one async gate per stripe, NOT one per task, so the
    // set never grows over the server's lifetime. A task always maps to the same
    // stripe (so its own RMW serialises); distinct tasks colliding on a stripe
    // just serialise harmlessly.
    private const int LockStripeCount = 64;

    private static readonly SemaphoreSlim[] Stripes =
        Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim LockFor(int serverTaskId) => Stripes[(uint)serverTaskId % LockStripeCount];

    public Task RecordDispatchedAsync(int serverTaskId, int machineId, string scriptTicket, CancellationToken cancellationToken = default)
        => MutateAsync(serverTaskId, current => InFlightScriptMap.Add(current, machineId, scriptTicket), cancellationToken);

    public Task ClearAsync(int serverTaskId, int machineId, CancellationToken cancellationToken = default)
        => MutateAsync(serverTaskId, current => InFlightScriptMap.Remove(current, machineId), cancellationToken);

    public async Task<string?> TryGetTicketAsync(int serverTaskId, int machineId, CancellationToken cancellationToken = default)
    {
        // The read MUST take the same per-task stripe as the writes. A parallel
        // batch dispatches to several targets at once on one Hangfire worker, and
        // every target's reattach probe lands here on the SAME scoped DbContext —
        // an ungated read races a concurrent read/RMW and EF throws "a second
        // operation was started on this context instance". The stripe serialises
        // all of a task's DbContext access onto one writer.
        var gate = LockFor(serverTaskId);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var row = await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == serverTaskId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            return row == null ? null : InFlightScriptMap.TryGet(row.InFlightScriptsJson ?? "{}", machineId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MutateAsync(int serverTaskId, Func<string, string> mutate, CancellationToken cancellationToken)
    {
        var gate = LockFor(serverTaskId);

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
