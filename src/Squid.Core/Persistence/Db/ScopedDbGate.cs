namespace Squid.Core.Persistence.Db;

/// <summary>
/// One async mutex per DI scope, and therefore per scoped <c>SquidDbContext</c>. An EF context allows a single
/// operation at a time; the deployment pipeline runs parallel steps and targets on one scope, so every scoped
/// service they reach (interruptions, checkpoints, feeds, the in-flight ledger) enters this gate around its
/// database work and the context is never used concurrently. Hold it only around the database call itself,
/// never around script execution, and never re-enter it from inside a gated call (it is not reentrant).
/// </summary>
public interface IScopedDbGate : IScopedDependency
{
    Task RunAsync(Func<Task> work, CancellationToken ct);

    Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct);
}

public sealed class ScopedDbGate : IScopedDbGate, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
