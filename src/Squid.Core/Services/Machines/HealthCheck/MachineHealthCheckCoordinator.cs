using System.Diagnostics;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.HealthCheck.Exceptions;
using Squid.Core.Halibut;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.HealthCheck;

public interface IMachineHealthCheckCoordinator : IScopedDependency
{
    /// <summary>Runs <paramref name="sweep"/> only if no other sweep is running in this process or on any replica. Returns false when skipped. The sweep token is cancelled at the sweep deadline; that ends the sweep quietly.</summary>
    Task<bool> TryRunSweepAsync(Func<CancellationToken, Task> sweep, CancellationToken ct);

    /// <summary>Runs <paramref name="work"/> while holding the machine's probe lock. Returns false when the lock stayed held by another probe for <paramref name="wait"/>.</summary>
    Task<bool> TryRunForMachineAsync(int machineId, TimeSpan wait, Func<Task> work, CancellationToken ct);

    /// <summary>Runs one probe with the configured per-probe timeout. A timeout yields an unhealthy result with a clear reason; cancellation of <paramref name="ct"/> propagates.</summary>
    Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct);

    /// <summary>
    /// Same as the two-argument overload with an explicit timeout (see <see cref="ProbeTimeoutFor"/>). A probe that reports a failure only after the timeout keeps its own error text in the result.
    /// The timeout is never shortened. Inside a sweep the probe starts only if its full timeout plus a grace to record the result fits in the sweep time left;
    /// otherwise it is not started and <see cref="SweepBudgetExhaustedException"/> is thrown (the machine stays due). If <paramref name="ct"/> is cancelled, no unhealthy result is returned.
    /// </summary>
    Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct, TimeSpan timeout);

    /// <summary>The probe timeout for a machine of this communication style: the operator's configured value, or the transport-aware default.</summary>
    TimeSpan ProbeTimeoutFor(CommunicationStyle style, MachineHealthCheckPolicyDto policy);

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
    private static readonly TimeSpan TimeoutGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(500);
    private static readonly object Ran = new();

    // Time the sweep must still have after a probe's full timeout: the 1s wait for the probe's own error text plus recording the result.
    internal static readonly TimeSpan SweepRecordGrace = TimeSpan.FromSeconds(5);

    // The deadline of the sweep this async flow belongs to; flows into every probe the sweep fans out, null for a manual check.
    // Set inside the async RunBoundedSweepAsync, so the runtime restores the caller's (null) value when it returns: it never outlives the sweep.
    private static readonly AsyncLocal<DateTimeOffset?> CurrentSweepDeadline = new();

    private readonly IRedisSafeRunner _redis;
    private readonly ILifetimeScope _scope;
    private readonly HealthCheckInProcessLocks _locks;
    private readonly MachineHealthCheckSetting _options;
    private readonly TimeProvider _time;

    public MachineHealthCheckCoordinator(IRedisSafeRunner redis, ILifetimeScope scope, HealthCheckInProcessLocks locks, MachineHealthCheckSetting options) : this(redis, scope, locks, options, null) { }

    internal MachineHealthCheckCoordinator(IRedisSafeRunner redis, ILifetimeScope scope, HealthCheckInProcessLocks locks, MachineHealthCheckSetting options, TimeProvider time)
    {
        _redis = redis;
        _scope = scope;
        _locks = locks;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan ManualBusyWait => ProbeTimeoutPolicy.ManualWait(_options, HalibutModule.BuildTimeoutsAndLimits()) + ManualWaitMargin;

    public TimeSpan ProbeTimeoutFor(CommunicationStyle style, MachineHealthCheckPolicyDto policy) => ProbeTimeoutPolicy.For(_options, style, policy, HalibutModule.BuildTimeoutsAndLimits());

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

            return await RunUnderRedisLockAsync(MachineLockKey(machineId), ProbeTimeoutPolicy.Longest(_options, HalibutModule.BuildTimeoutsAndLimits()) + LockMargin, redisWait, work).ConfigureAwait(false);
        }
        finally
        {
            _locks.ExitMachine(machineId);
        }
    }

    // The caller's wait is ONE budget shared by the in-process lock and the Redis lock, not one per lock.
    private static TimeSpan RemainingWait(TimeSpan wait, TimeSpan elapsed) => elapsed >= wait ? TimeSpan.Zero : wait - elapsed;

    public Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct) => RunProbeAsync(probe, ct, _options.ProbeTimeout);

    public async Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct, TimeSpan timeout)
    {
        EnsureProbeFitsInSweep(timeout);

        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var running = probe(probeCts.Token);

        try
        {
            var result = await running.WaitAsync(probeCts.Token).ConfigureAwait(false);

            return ResolveProbeOutcome(result, probeCts, ct, timeout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timedOut = await BuildTimeoutResultAsync(running, timeout).ConfigureAwait(false);

            // The caller (e.g. the sweep deadline) gave up while we waited for the probe's own error text: report nothing rather than a verdict.
            ct.ThrowIfCancellationRequested();

            return timedOut;
        }
    }

    // Inside a sweep a probe starts only if its FULL timeout plus the recording grace fits in the sweep time left, so it always ends on its own
    // timeout before the deadline and every result the sweep records is genuine. One that does not fit is deferred: not started, nothing
    // recorded, the machine stays due (and is first in the next sweep). A timeout that cannot fit in any sweep runs as before; if the deadline
    // cuts it, it records nothing. Outside a sweep (manual check) nothing applies.
    private void EnsureProbeFitsInSweep(TimeSpan timeout)
    {
        var deadline = CurrentSweepDeadline.Value;
        var needed = timeout + SweepRecordGrace;

        if (deadline == null) return;

        if (needed > _options.SweepDeadline)
        {
            Log.Warning("Health check probe timeout {TimeoutSeconds}s plus {GraceSeconds}s to record exceeds the {DeadlineSeconds}s sweep deadline; a probe still running when the sweep ends records nothing", timeout.TotalSeconds, SweepRecordGrace.TotalSeconds, _options.SweepDeadline.TotalSeconds);
            return;
        }

        var remaining = deadline.Value - _time.GetUtcNow();

        if (needed > remaining)
            throw new SweepBudgetExhaustedException(timeout, remaining);
    }

    // The probe's token is already cancelled, so a well-behaved probe finishes within the grace and reports why it was failing
    // (connection refused, auth error, ...); one that is truly hung yields the plain timeout message.
    private async Task<HealthCheckResult> BuildTimeoutResultAsync(Task<HealthCheckResult> running, TimeSpan timeout)
    {
        try
        {
            var result = await running.WaitAsync(TimeoutGrace, _time).ConfigureAwait(false);

            return BuildTimeoutResult(timeout, result.Detail);
        }
        catch (Exception)
        {
            return BuildTimeoutResult(timeout, null);
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
        CurrentSweepDeadline.Value = _time.GetUtcNow() + _options.SweepDeadline;

        using var deadlineTimerCts = new CancellationTokenSource(_options.SweepDeadline, _time);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineTimerCts.Token);

        try
        {
            await sweep(deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Health check sweep hit its {DeadlineSeconds}s deadline; machines not yet probed stay due for the next sweep", _options.SweepDeadline.TotalSeconds);
        }
    }

    private static HealthCheckResult ResolveProbeOutcome(HealthCheckResult result, CancellationTokenSource probeCts, CancellationToken ct, TimeSpan timeout)
    {
        if (result.Healthy) return result;

        ct.ThrowIfCancellationRequested();

        return probeCts.IsCancellationRequested ? BuildTimeoutResult(timeout, result.Detail) : result;
    }

    private static HealthCheckResult BuildTimeoutResult(TimeSpan timeout, string probeDetail)
    {
        var message = $"Health check timed out after {timeout.TotalSeconds:0}s without a response from the machine";

        return new HealthCheckResult(false, string.IsNullOrWhiteSpace(probeDetail) ? message : $"{message}. Last probe error: {probeDetail}");
    }

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
