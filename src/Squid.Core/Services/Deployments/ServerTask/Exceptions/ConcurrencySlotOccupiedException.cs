namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

/// <summary>
/// Thrown when a task cannot transition to <see cref="TaskState.Executing"/> because another
/// task with the SAME <c>ConcurrencyTag</c> is already ACTIVE (Executing, Paused, or
/// Cancelling) — the cross-process atomic slot guarantee. It is raised when the DB rejects the
/// transition with a unique-constraint violation (23505) on <c>ux_server_task_active_per_tag</c>,
/// i.e. a second pod won the slot in the TOCTOU window between the runner's free-slot check and
/// the claim.
///
/// <para>Distinct from <see cref="ServerTaskStateTransitionException"/> (an illegal state
/// transition): this is a CONTENTION signal, not an error. The deployment runner catches it
/// and re-enqueues the task (it stays Pending, or Paused on a resume) to retry the slot later,
/// rather than failing — so the loser is never lost and the two deployments never overlap.</para>
/// </summary>
public sealed class ConcurrencySlotOccupiedException : InvalidOperationException
{
    public string ConcurrencyTag { get; }

    public ConcurrencySlotOccupiedException(string concurrencyTag)
        : base($"Concurrency slot for tag '{concurrencyTag}' is already occupied by another active deployment")
    {
        ConcurrencyTag = concurrencyTag;
    }
}
