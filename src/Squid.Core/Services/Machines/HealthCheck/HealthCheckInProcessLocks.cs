using System.Collections.Concurrent;

namespace Squid.Core.Services.Machines.HealthCheck;

/// <summary>
/// Per-process exclusion that backs the Redis locks: it is always enforced (so a Redis outage
/// degrades to "one sweep / one probe per machine per replica" instead of unbounded overlap)
/// and is shared by every scope in the process.
/// </summary>
public sealed class HealthCheckInProcessLocks : ISingletonDependency
{
    private readonly SemaphoreSlim _sweep = new(1, 1);
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _machines = new();

    public Task<bool> TryEnterSweepAsync() => _sweep.WaitAsync(TimeSpan.Zero);

    public void ExitSweep() => _sweep.Release();

    public Task<bool> TryEnterMachineAsync(int machineId, TimeSpan wait, CancellationToken ct) => MachineLock(machineId).WaitAsync(wait, ct);

    public void ExitMachine(int machineId) => MachineLock(machineId).Release();

    private SemaphoreSlim MachineLock(int machineId) => _machines.GetOrAdd(machineId, _ => new SemaphoreSlim(1, 1));
}
