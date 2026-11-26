using System.Collections.Concurrent;

namespace Squid.Core.Services.Machines.HealthCheck;

/// <summary>
/// Per-process health-check state shared by every scope in the process: the exclusion that backs the Redis locks (always enforced, so a Redis
/// outage degrades to "one sweep / one probe per machine per replica" instead of unbounded overlap), and when this process last started a probe
/// of each machine, which keeps the sweep order fair (see <c>MachineHealthCheckCoordinator.InSweepOrder</c>).
/// </summary>
public sealed class HealthCheckInProcessLocks : ISingletonDependency
{
    private readonly SemaphoreSlim _sweep = new(1, 1);
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _machines = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _probeStarts = new();

    public Task<bool> TryEnterSweepAsync() => _sweep.WaitAsync(TimeSpan.Zero);

    public void ExitSweep() => _sweep.Release();

    public Task<bool> TryEnterMachineAsync(int machineId, TimeSpan wait, CancellationToken ct) => MachineLock(machineId).WaitAsync(wait, ct);

    public void ExitMachine(int machineId) => MachineLock(machineId).Release();

    public void NoteProbeStarted(int machineId, DateTimeOffset startedAt) => _probeStarts[machineId] = startedAt;

    public DateTimeOffset? LastProbeStarted(int machineId) => _probeStarts.TryGetValue(machineId, out var startedAt) ? startedAt : null;

    private SemaphoreSlim MachineLock(int machineId) => _machines.GetOrAdd(machineId, _ => new SemaphoreSlim(1, 1));
}
