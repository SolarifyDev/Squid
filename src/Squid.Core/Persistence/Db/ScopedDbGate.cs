namespace Squid.Core.Persistence.Db;

/// <summary>
/// One async mutex per DI scope, and therefore per scoped <c>SquidDbContext</c>. An EF context allows a single
/// operation at a time; the deployment pipeline runs parallel steps and targets on one scope, so every scoped
/// service they reach (interruptions, checkpoints, feeds, accounts, the in-flight ledger) enters this gate around
/// its database work and the context is never used concurrently. Hold it only around the database call itself,
/// never around script execution. It is not reentrant: entering it from inside a gated call (or from a task that
/// call started) would wait on itself forever, so that is detected and thrown instead.
/// </summary>
public interface IScopedDbGate : IScopedDependency
{
    Task RunAsync(Func<Task> work, CancellationToken ct);

    Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct);
}

public sealed class ScopedDbGate : IScopedDbGate, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _heldByThisFlow = new();

    public async Task RunAsync(Func<Task> work, CancellationToken ct)
    {
        EnsureNotReentered();

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        // Set here, not in a helper: an async helper's AsyncLocal writes do not flow back to its caller.
        _heldByThisFlow.Value = true;

        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        EnsureNotReentered();

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        // Set here, not in a helper: an async helper's AsyncLocal writes do not flow back to its caller.
        _heldByThisFlow.Value = true;

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    private void EnsureNotReentered()
    {
        if (_heldByThisFlow.Value)
            throw new InvalidOperationException("The scoped database gate was entered again from inside a gated call, which would deadlock. Gate only the leaf database call, and never call another gated service from inside it.");
    }

    private void Exit()
    {
        _heldByThisFlow.Value = false;
        _gate.Release();
    }

    public void Dispose() => _gate.Dispose();
}
