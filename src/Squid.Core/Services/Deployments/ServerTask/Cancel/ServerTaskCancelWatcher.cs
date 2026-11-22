namespace Squid.Core.Services.Deployments.ServerTask.Cancel;

/// <summary>
/// Carries a user's cancel to the pod that actually runs the pipeline. The cancel request lands on an arbitrary pod, which can only signal
/// its own in-process registry; the database row (<c>Cancelling</c>) is the one thing every pod sees, so the running pod polls it and
/// invokes <c>onCancelRequested</c> — the runner passes the same registry cancel a same-pod cancel uses, so every downstream semantic is identical.
/// </summary>
public interface IServerTaskCancelWatcher : IScopedDependency
{
    IAsyncDisposable Start(int serverTaskId, Action onCancelRequested);
}

public sealed class ServerTaskCancelWatcher : IServerTaskCancelWatcher
{
    private readonly ILifetimeScope _lifetimeScope;
    private readonly ServerTaskCancelWatchOptions _options;

    public ServerTaskCancelWatcher(ILifetimeScope lifetimeScope) : this(lifetimeScope, ServerTaskCancelWatchOptions.FromEnvironment())
    {
    }

    internal ServerTaskCancelWatcher(ILifetimeScope lifetimeScope, ServerTaskCancelWatchOptions options)
    {
        _lifetimeScope = lifetimeScope;
        _options = options;
    }

    public IAsyncDisposable Start(int serverTaskId, Action onCancelRequested) => new Watch(this, serverTaskId, onCancelRequested);

    // Each probe on its own scope: its own DbContext and connection, never inside the pipeline's open transaction.
    private async Task<bool> IsCancelRequestedAsync(int serverTaskId, CancellationToken ct)
    {
        await using var scope = _lifetimeScope.BeginLifetimeScope();

        return await scope.Resolve<IServerTaskDataProvider>().IsCancelRequestedAsync(serverTaskId, ct).ConfigureAwait(false);
    }

    private sealed class Watch : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Watch(ServerTaskCancelWatcher watcher, int serverTaskId, Action onCancelRequested) => _loop = LoopAsync(watcher, serverTaskId, onCancelRequested, _stop.Token);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            await _loop.ConfigureAwait(false);

            _stop.Dispose();
        }

        // Stops after the first observed cancel: the signal is one-shot and the runner tears the watch down on exit anyway.
        private async Task LoopAsync(ServerTaskCancelWatcher watcher, int serverTaskId, Action onCancelRequested, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(watcher._options.PollInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (await TryProbeAsync(watcher, serverTaskId, ct).ConfigureAwait(false) && TrySignal(serverTaskId, onCancelRequested)) return;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        // A probe failure (DB blip) is logged and retried next tick — never read as a cancel.
        private static async Task<bool> TryProbeAsync(ServerTaskCancelWatcher watcher, int serverTaskId, CancellationToken ct)
        {
            try
            {
                return await watcher.IsCancelRequestedAsync(serverTaskId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[CancelWatch] Task {TaskId} cancel probe failed; retrying next interval", serverTaskId);

                return false;
            }
        }

        private static bool TrySignal(int serverTaskId, Action onCancelRequested)
        {
            Log.Information("[CancelWatch] Task {TaskId} was cancelled from another process; cancelling the running pipeline", serverTaskId);

            try
            {
                onCancelRequested();

                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[CancelWatch] Task {TaskId} cancel signal failed; retrying next interval", serverTaskId);

                return false;
            }
        }
    }
}
