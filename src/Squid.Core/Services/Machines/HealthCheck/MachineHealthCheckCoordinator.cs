using System.Diagnostics;
using Squid.Core.Persistence.Entities.Deployments;
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
    /// The timeout is never shortened. Inside a sweep the probe starts if its full timeout plus a grace to record the result fits in the sweep time left. It also starts,
    /// without that guarantee, when it is one of the first <c>MaxParallelism</c> machines of the sweep order or could not have fitted anywhere in this sweep's probing time;
    /// otherwise it is not started and <see cref="SweepBudgetExhaustedException"/> is thrown (the machine stays due). If <paramref name="ct"/> is cancelled, no unhealthy result is returned.
    /// </summary>
    Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct, TimeSpan timeout);

    /// <summary>The probe timeout for a machine of this communication style: the operator's configured value, or the transport-aware default.</summary>
    TimeSpan ProbeTimeoutFor(CommunicationStyle style, MachineHealthCheckPolicyDto policy, MachineConnectivityPolicyDto connectivity = null);

    /// <summary>Runs <paramref name="work"/> for each item with bounded parallelism, each in its own DI scope (own DbContext). A failing item never stops the others. Inside a sweep, an item's position in <paramref name="items"/> decides whether it is the head of the sweep order.</summary>
    Task ForEachInScopeAsync<T>(IReadOnlyList<T> items, Func<ILifetimeScope, T, CancellationToken, Task> work, CancellationToken ct);

    /// <summary>Records that a probe of the machine started at <paramref name="startedAt"/> (the clock <c>HealthLastChecked</c> is written with). Kept in memory for this process.</summary>
    void NoteProbeStarted(int machineId, DateTimeOffset startedAt);

    /// <summary>
    /// The ids of <paramref name="dueMachines"/> least recently ATTEMPTED first: ordered by the later of the machine's last recorded check and the last probe this process
    /// started for it, keeping the given order among equals. A machine whose probe the sweep deadline cut records nothing and keeps its old check time; this moves it behind
    /// every machine not started since, so it cannot hold the head of the sweep order forever.
    /// </summary>
    IReadOnlyList<int> InSweepOrder(IReadOnlyList<Machine> dueMachines);

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

    // The sweep this async flow belongs to; flows into every probe the sweep fans out, null for a manual check.
    // Set inside the async RunBoundedSweepAsync, so the runtime restores the caller's (null) value when it returns: it never outlives the sweep.
    private static readonly AsyncLocal<SweepState> CurrentSweep = new();

    // The position in the sweep order of the item this flow probes; set inside the async RunItemInScopeAsync, so it is scoped to that item.
    private static readonly AsyncLocal<int?> CurrentSweepPosition = new();

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

    public TimeSpan ProbeTimeoutFor(CommunicationStyle style, MachineHealthCheckPolicyDto policy, MachineConnectivityPolicyDto connectivity = null) => ProbeTimeoutPolicy.For(_options, style, policy, HalibutModule.BuildTimeoutsAndLimits(), connectivity);

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

    // Inside a sweep a probe whose FULL timeout plus the recording grace fits in the sweep time left starts, ends on its own timeout before the
    // deadline, and its result is genuine. One that does not fit is deferred (not started, nothing recorded, stays due) only when a later sweep can
    // do better: it could have fitted this sweep's probing time and it is not at the head of the sweep order. The head is always started, and so is a
    // probe that cannot fit the probing time at all, else the same machines would be deferred in every sweep; if the deadline cuts such a probe it
    // records nothing, and the sweep order moves its machine behind the ones not started since. The timeout is never shortened. A manual check is never deferred.
    private void EnsureProbeFitsInSweep(TimeSpan timeout)
    {
        var sweep = CurrentSweep.Value;

        if (sweep == null) return;

        var needed = ProbeTimeoutPolicy.SweepNeed(timeout);
        var remaining = sweep.Deadline - _time.GetUtcNow();

        if (needed <= remaining) return;

        if (IsHeadOfSweep() || needed > sweep.ProbingWindow)
        {
            Log.Warning("Health check probe needs {NeededSeconds}s (timeout plus time to record) but the sweep has {RemainingSeconds}s left; starting it anyway so the machine is not deferred in every sweep, it records nothing if the sweep deadline ends it first", needed.TotalSeconds, remaining.TotalSeconds);
            return;
        }

        throw new SweepBudgetExhaustedException(timeout, remaining);
    }

    private bool IsHeadOfSweep() => CurrentSweepPosition.Value < _options.MaxParallelism;

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

        MarkProbingStarted();

        await Parallel.ForEachAsync(items.Select((item, position) => (item, position)), parallel, (entry, token) => new ValueTask(RunItemInScopeAsync(entry.item, entry.position, work, token))).ConfigureAwait(false);
    }

    // What a probe can fit is measured against the sweep time left once listing the due machines is done, not against the whole sweep deadline.
    private void MarkProbingStarted()
    {
        var sweep = CurrentSweep.Value;

        if (sweep == null) return;

        sweep.ProbingWindow = sweep.Deadline - _time.GetUtcNow();
    }

    private async Task RunItemInScopeAsync<T>(T item, int position, Func<ILifetimeScope, T, CancellationToken, Task> work, CancellationToken ct)
    {
        CurrentSweepPosition.Value = position;

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

    public void NoteProbeStarted(int machineId, DateTimeOffset startedAt) => _locks.NoteProbeStarted(machineId, startedAt);

    public IReadOnlyList<int> InSweepOrder(IReadOnlyList<Machine> dueMachines) => dueMachines.OrderBy(LastAttempted).Select(m => m.Id).ToList();

    private DateTimeOffset? LastAttempted(Machine machine) => Later(machine.HealthLastChecked, _locks.LastProbeStarted(machine.Id));

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a == null || b > a ? b : a;

    private async Task RunBoundedSweepAsync(Func<CancellationToken, Task> sweep, CancellationToken ct)
    {
        CurrentSweep.Value = new SweepState(_time.GetUtcNow() + _options.SweepDeadline, _options.SweepDeadline);

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

    // ProbingWindow starts as the whole sweep and is narrowed to the time left when the sweep fans its probes out.
    private sealed class SweepState(DateTimeOffset deadline, TimeSpan probingWindow)
    {
        public DateTimeOffset Deadline { get; } = deadline;

        public TimeSpan ProbingWindow { get; set; } = probingWindow;
    }
}
