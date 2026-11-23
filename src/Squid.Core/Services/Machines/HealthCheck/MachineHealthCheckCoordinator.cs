using System.Diagnostics;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Settings.Machines;

namespace Squid.Core.Services.Machines.HealthCheck;

public interface IMachineHealthCheckCoordinator : IScopedDependency
{
    /// <summary>Runs <paramref name="sweep"/> only if no other sweep is running in this process or on any replica. Returns false when skipped. The sweep token is cancelled at the sweep deadline; that ends the sweep quietly.</summary>
    Task<bool> TryRunSweepAsync(Func<CancellationToken, Task> sweep, CancellationToken ct);

    /// <summary>Runs <paramref name="work"/> while holding the machine's probe lock. Returns false when the lock stayed held by another probe for <paramref name="wait"/>.</summary>
    Task<bool> TryRunForMachineAsync(int machineId, TimeSpan wait, Func<Task> work, CancellationToken ct);

    /// <summary>Runs one probe with the per-probe timeout. A timeout yields an unhealthy result with a clear reason; cancellation of <paramref name="ct"/> propagates.</summary>
    Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct);

    /// <summary>Runs <paramref name="work"/> for each item with bounded parallelism, each in its own DI scope (own DbContext). A failing item never stops the others.</summary>
    Task ForEachInScopeAsync<T>(IReadOnlyList<T> items, Func<ILifetimeScope, T, CancellationToken, Task> work, CancellationToken ct);

    /// <summary>How long a manual check waits for a probe already running on the same machine.</summary>
    TimeSpan ManualBusyWait { get; }
}

public sealed class MachineHealthCheckCoordinator : IMachineHealthCheckCoordinator
{
    public const string SweepLockKey = "squid:health:sweep";

    private const string MachineLockKeyPrefix = "squid:health:machine:";
    private static readonly TimeSpan LockMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ManualWaitMargin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(500);
    private static readonly object Ran = new();

    private readonly IRedisSafeRunner _redis;
    private readonly ILifetimeScope _scope;
    private readonly HealthCheckInProcessLocks _locks;
    private readonly MachineHealthCheckSetting _options;

    public MachineHealthCheckCoordinator(IRedisSafeRunner redis, ILifetimeScope scope, HealthCheckInProcessLocks locks, MachineHealthCheckSetting options)
    {
        _redis = redis;
        _scope = scope;
        _locks = locks;
        _options = options;
    }

    public TimeSpan ManualBusyWait => _options.ProbeTimeout + ManualWaitMargin;

    internal static string MachineLockKey(int machineId) => $"{MachineLockKeyPrefix}{machineId}";

    public async Task<bool> TryRunSweepAsync(Func<CancellationToken, Task> sweep, CancellationToken ct)
    {
        if (!await _locks.TryEnterSweepAsync().ConfigureAwait(false)) return false;

        try
        {
            return await RunUnderRedisLockAsync(SweepLockKey, _options.SweepDeadline + LockMargin, null, () => RunBoundedSweepAsync(sweep, ct)).ConfigureAwait(false);
        }
        finally
        {
            _locks.ExitSweep();
        }
    }

    public async Task<bool> TryRunForMachineAsync(int machineId, TimeSpan wait, Func<Task> work, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();

        if (!await _locks.TryEnterMachineAsync(machineId, wait, ct).ConfigureAwait(false)) return false;

        try
        {
            var redisWait = RemainingWait(wait, waited.Elapsed);

            return await RunUnderRedisLockAsync(MachineLockKey(machineId), _options.ProbeTimeout + LockMargin, redisWait, work).ConfigureAwait(false);
        }
        finally
        {
            _locks.ExitMachine(machineId);
        }
    }

    // The caller's wait is ONE budget shared by the in-process lock and the Redis lock, not one per lock.
    private static TimeSpan RemainingWait(TimeSpan wait, TimeSpan elapsed) => elapsed >= wait ? TimeSpan.Zero : wait - elapsed;

    public async Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(_options.ProbeTimeout);

        try
        {
            var result = await probe(probeCts.Token).WaitAsync(probeCts.Token).ConfigureAwait(false);

            return ResolveProbeOutcome(result, probeCts, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return BuildTimeoutResult();
        }
    }

    public async Task ForEachInScopeAsync<T>(IReadOnlyList<T> items, Func<ILifetimeScope, T, CancellationToken, Task> work, CancellationToken ct)
    {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelism, CancellationToken = ct };

        await Parallel.ForEachAsync(items, parallel, (item, token) => new ValueTask(RunItemInScopeAsync(item, work, token))).ConfigureAwait(false);
    }

    private async Task RunItemInScopeAsync<T>(T item, Func<ILifetimeScope, T, CancellationToken, Task> work, CancellationToken ct)
    {
        try
        {
            await using var scope = _scope.BeginLifetimeScope();

            await work(scope, item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health check sweep item {Item} failed", item);
        }
    }

    private async Task RunBoundedSweepAsync(Func<CancellationToken, Task> sweep, CancellationToken ct)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(_options.SweepDeadline);

        try
        {
            await sweep(deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Health check sweep hit its {DeadlineSeconds}s deadline; machines not yet probed stay due for the next sweep", _options.SweepDeadline.TotalSeconds);
        }
    }

    private HealthCheckResult ResolveProbeOutcome(HealthCheckResult result, CancellationTokenSource probeCts, CancellationToken ct)
    {
        if (result.Healthy) return result;

        ct.ThrowIfCancellationRequested();

        return probeCts.IsCancellationRequested ? BuildTimeoutResult() : result;
    }

    private HealthCheckResult BuildTimeoutResult()
        => new(false, $"Health check timed out after {_options.ProbeTimeout.TotalSeconds:0}s without a response from the machine");

    private async Task<bool> RunUnderRedisLockAsync(string key, TimeSpan expiry, TimeSpan? wait, Func<Task> work)
    {
        var started = false;
        var retry = wait > TimeSpan.Zero ? LockRetry : (TimeSpan?)null;
        var lockWait = retry == null ? null : wait;

        try
        {
            var ran = await _redis.ExecuteWithLockAsync<object>(key, async () =>
            {
                started = true;
                await work().ConfigureAwait(false);

                return Ran;
            }, expiry, lockWait, retry).ConfigureAwait(false);

            return ran != null;
        }
        catch (LockAcquireFailedException ex) when (!started)
        {
            Log.Warning(ex, "Redis unavailable for health check lock {LockKey}; continuing under the in-process lock only", key);
            await work().ConfigureAwait(false);

            return true;
        }
    }
}
