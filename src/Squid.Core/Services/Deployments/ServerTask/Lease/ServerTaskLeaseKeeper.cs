using Squid.Core.Settings.ServerTask;

namespace Squid.Core.Services.Deployments.ServerTask.Lease;

/// <summary>
/// Proves the process running a task is alive: while the returned lease is held, the task's lease is renewed every interval on a timer
/// of its own, independent of the pipeline's progress, so a deployment blocked in a long script still renews and one whose process
/// died does not.
/// </summary>
public interface IServerTaskLeaseKeeper : IScopedDependency
{
    IServerTaskLease Start(int serverTaskId);
}

public interface IServerTaskLease : IAsyncDisposable
{
    /// <summary>True once a lease this process held was taken away (the reaper recovered the task, or another process took it over). Renewal has stopped.</summary>
    bool IsLost { get; }
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

    public IServerTaskLease Start(int serverTaskId) => new Lease(this, serverTaskId);

    // Each beat on its own scope: its own DbContext and connection, so it lands at once instead of inside the pipeline's open transaction.
    private async Task<int> BeatAsync(int serverTaskId, bool held, CancellationToken ct)
    {
        await using var scope = _lifetimeScope.BeginLifetimeScope();

        var provider = scope.Resolve<IServerTaskLeaseDataProvider>();

        return held
            ? await provider.RenewAsync(serverTaskId, ServerTaskLeaseOwner.Current, ct).ConfigureAwait(false)
            : await provider.AcquireAsync(serverTaskId, ServerTaskLeaseOwner.Current, _options.Stale, ct).ConfigureAwait(false);
    }

    private sealed class Lease : IServerTaskLease
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private volatile bool _lost;
        private bool _warnedDenied;

        public Lease(ServerTaskLeaseKeeper keeper, int serverTaskId) => _loop = LoopAsync(keeper, serverTaskId, _stop.Token);

        public bool IsLost => _lost;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            await _loop.ConfigureAwait(false);

            _stop.Dispose();
        }

        // Acquire right away and on every tick until the claim lands (the task may still be Pending/Paused when the loop starts); once held, only
        // renew — a renewal that then updates nothing means the lease was taken away, and renewing stops for good so it never undoes the recovery.
        private async Task LoopAsync(ServerTaskLeaseKeeper keeper, int serverTaskId, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(keeper._options.RenewInterval, keeper._time);
            var held = false;

            try
            {
                do
                {
                    held = await TryBeatAsync(keeper, serverTaskId, held, ct).ConfigureAwait(false);
                }
                while (!_lost && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<bool> TryBeatAsync(ServerTaskLeaseKeeper keeper, int serverTaskId, bool held, CancellationToken ct)
        {
            try
            {
                var rows = await keeper.BeatAsync(serverTaskId, held, ct).ConfigureAwait(false);

                if (rows > 0) return true;

                if (held) MarkLost(serverTaskId);
                else WarnDeniedOnce(serverTaskId);

                return held;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[Lease] Task {TaskId} lease renewal failed; the deployment continues and renewal retries next interval", serverTaskId);

                return held;
            }
        }

        // The keeper starts after the claim, which already fenced out a live foreign owner, so a denial here means someone else took the lease in
        // between. It is not "lost" (this process never held it) and not fatal: it warns once and keeps trying each interval, so it neither
        // flags the pipeline as dispossessed nor spins silently.
        private void WarnDeniedOnce(int serverTaskId)
        {
            if (_warnedDenied) return;

            _warnedDenied = true;

            Log.Warning("[Lease] Task {TaskId} lease is held by another live process; this process keeps trying to acquire it each interval", serverTaskId);
        }

        private void MarkLost(int serverTaskId)
        {
            _lost = true;

            Log.Warning("[Lease] Task {TaskId} lease is no longer held by this process (recovered by the reaper, finished, or taken over); renewal stopped", serverTaskId);
        }
    }
}
