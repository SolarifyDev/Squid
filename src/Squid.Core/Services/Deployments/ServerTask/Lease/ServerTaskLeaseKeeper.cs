using Squid.Core.Settings.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask.Lease;

/// <summary>
/// Proves the run executing a task is alive: while the returned lease is held, the task's lease is renewed every interval on a timer
/// of its own, independent of the pipeline's progress, so a deployment blocked in a long script still renews and one whose process
/// died does not.
/// </summary>
public interface IServerTaskLeaseKeeper : IScopedDependency
{
    /// <summary>Renews the lease on behalf of this process (<see cref="ServerTaskLeaseOwner.Current"/>); a pipeline run uses the overload that names the run.</summary>
    IServerTaskLease Start(int serverTaskId);

    /// <summary>Renews the lease on behalf of one pipeline run, so a second run of the same task in this process is a different owner.</summary>
    IServerTaskLease Start(int serverTaskId, string leaseOwner);
}

public interface IServerTaskLease : IAsyncDisposable
{
    /// <summary>
    /// True once the lease this run held was taken away (the reaper recovered the task, another run took it over) or could no longer be proven (renewals
    /// kept failing for nearly the stale window, after which the reaper may legitimately recover the task). Renewal has stopped.
    /// </summary>
    bool IsLost { get; }

    /// <summary>Cancelled the moment <see cref="IsLost"/> becomes true, so the run can stop working on a task it no longer owns.</summary>
    CancellationToken LostToken { get; }

    /// <summary>
    /// The run is about to give the lease up itself (it pauses its own task, which clears the lease). Renewal stops at once, in the middle of a beat too,
    /// so no renewal ever finds the cleared lease and reads the run's own pause as a loss: the run still has to record how it ended.
    /// </summary>
    void Release();
}

public sealed class ServerTaskLeaseKeeper : IServerTaskLeaseKeeper
{
    private readonly ILifetimeScope _lifetimeScope;
    private readonly TimeProvider _time;
    private readonly ServerTaskLeaseOptions _options;

    public ServerTaskLeaseKeeper(ILifetimeScope lifetimeScope, ServerTaskSetting setting) : this(lifetimeScope, TimeProvider.System, ServerTaskLeaseOptions.From(setting))
    {
    }

    internal ServerTaskLeaseKeeper(ILifetimeScope lifetimeScope, TimeProvider time, ServerTaskLeaseOptions options)
    {
        _lifetimeScope = lifetimeScope;
        _time = time;
        _options = options;
    }

    public IServerTaskLease Start(int serverTaskId) => Start(serverTaskId, ServerTaskLeaseOwner.Current);

    public IServerTaskLease Start(int serverTaskId, string leaseOwner) => new Lease(this, serverTaskId, leaseOwner);

    // Each beat on its own scope: its own DbContext and connection, so it lands at once instead of inside the pipeline's open transaction.
    private async Task<int> BeatAsync(int serverTaskId, string owner, bool held, CancellationToken ct)
    {
        await using var scope = _lifetimeScope.BeginLifetimeScope();

        var provider = scope.Resolve<IServerTaskLeaseDataProvider>();

        return held
            ? await provider.RenewAsync(serverTaskId, owner, ct).ConfigureAwait(false)
            : await provider.AcquireAsync(serverTaskId, owner, _options.Stale, ct).ConfigureAwait(false);
    }

    private sealed class Lease : IServerTaskLease
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _lostSource = new();
        private readonly Task _loop;
        private long _provenAt;
        private volatile bool _lost;
        private bool _warnedDenied;

        public Lease(ServerTaskLeaseKeeper keeper, int serverTaskId, string owner)
        {
            _provenAt = keeper._time.GetTimestamp();
            _loop = LoopAsync(keeper, serverTaskId, owner, _stop.Token);
        }

        public bool IsLost => _lost;

        public CancellationToken LostToken => _lostSource.Token;

        public void Release() => _stop.Cancel();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            await _loop.ConfigureAwait(false);

            _stop.Dispose();
        }

        // Acquire right away and on every tick until the claim lands (the task may still be Pending/Paused when the loop starts); once held, only
        // renew — a renewal that then updates nothing means the lease was taken away, and renewing stops for good so it never undoes the recovery.
        private async Task LoopAsync(ServerTaskLeaseKeeper keeper, int serverTaskId, string owner, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(keeper._options.RenewInterval, keeper._time);
            var held = false;

            try
            {
                do
                {
                    held = await TryBeatAsync(keeper, serverTaskId, owner, held, ct).ConfigureAwait(false);
                }
                while (!_lost && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<bool> TryBeatAsync(ServerTaskLeaseKeeper keeper, int serverTaskId, string owner, bool held, CancellationToken ct)
        {
            try
            {
                var rows = await BeatWithinAnIntervalAsync(keeper, serverTaskId, owner, held, ct).ConfigureAwait(false);

                if (rows > 0)
                {
                    _provenAt = keeper._time.GetTimestamp();

                    return true;
                }

                if (held) MarkLost(serverTaskId);
                else WarnDeniedOnce(serverTaskId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Warning(ex, "[Lease] Task {TaskId} lease renewal failed; the deployment continues and renewal retries next interval", serverTaskId);
            }

            FenceWhenUnproven(keeper, serverTaskId);

            return held;
        }

        // A beat that hangs (pool exhaustion, a stalled connection) must not stop the loop from noticing that nothing has been proven for too long.
        private static async Task<int> BeatWithinAnIntervalAsync(ServerTaskLeaseKeeper keeper, int serverTaskId, string owner, bool held, CancellationToken ct)
        {
            var beat = keeper.BeatAsync(serverTaskId, owner, held, ct);

            try
            {
                return await beat.WaitAsync(keeper._options.RenewInterval, keeper._time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = beat.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);

                throw;
            }
        }

        // The keeper started right after the claim stamped this run's lease, so a denial means someone took it in between. It is not fatal here: it warns
        // once and keeps trying each interval; if nothing is proven for the self-fence window the lease is treated as lost like any other silent one.
        private void WarnDeniedOnce(int serverTaskId)
        {
            if (_warnedDenied) return;

            _warnedDenied = true;

            Log.Warning("[Lease] Task {TaskId} lease is held by another live run; this run keeps trying to acquire it each interval", serverTaskId);
        }

        // Past the self-fence window without one successful renewal the reaper may already be recovering the task (its window is the full stale
        // threshold, measured on the database clock), so this run must assume it is lost even though the database never told it so.
        private void FenceWhenUnproven(ServerTaskLeaseKeeper keeper, int serverTaskId)
        {
            if (_lost) return;

            var silent = keeper._time.GetElapsedTime(Interlocked.Read(ref _provenAt));

            if (silent < keeper._options.SelfFence) return;

            Log.Warning("[Lease] Task {TaskId} lease could not be renewed for {SilentSeconds}s (self-fence after {FenceSeconds}s); assuming it was lost", serverTaskId, (int)silent.TotalSeconds, (int)keeper._options.SelfFence.TotalSeconds);

            MarkLost();
        }

        private void MarkLost(int serverTaskId)
        {
            Log.Warning("[Lease] Task {TaskId} lease is no longer held by this run (recovered by the reaper, finished, or taken over); renewal stopped", serverTaskId);

            MarkLost();
        }

        private void MarkLost()
        {
            _lost = true;

            try
            {
                _lostSource.Cancel();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Lease] A listener of the lease-lost signal threw");
            }
        }
    }
}
