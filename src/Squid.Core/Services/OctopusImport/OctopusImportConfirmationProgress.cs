namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Writes a running confirmation's live progress: while the returned run is held, the tracker's snapshot is written every interval when it
/// changed, on a scope of its own, never inside the import transaction, and never awaited by the import.
/// </summary>
public interface IOctopusImportConfirmationProgress : IScopedDependency
{
    IOctopusImportConfirmationProgressRun Start(Guid sessionId, OctopusImportConfirmationProgressTracker tracker);
}

/// <summary>A running progress writer. Disposing it stops the loop and writes the final snapshot.</summary>
public interface IOctopusImportConfirmationProgressRun : IAsyncDisposable
{
    /// <summary>Writes the snapshot now if it changed since the last write; never throws.</summary>
    Task FlushAsync();
}

public sealed class OctopusImportConfirmationProgress : IOctopusImportConfirmationProgress
{
    public static readonly TimeSpan FinalFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly ILifetimeScope _lifetimeScope;
    private readonly TimeProvider _time;
    private readonly OctopusImportConfirmationResilienceOptions _options;

    public OctopusImportConfirmationProgress(ILifetimeScope lifetimeScope) : this(lifetimeScope, TimeProvider.System, OctopusImportConfirmationResilienceOptions.FromEnvironment())
    {
    }

    internal OctopusImportConfirmationProgress(ILifetimeScope lifetimeScope, TimeProvider time, OctopusImportConfirmationResilienceOptions options)
    {
        _lifetimeScope = lifetimeScope;
        _time = time;
        _options = options;
    }

    public IOctopusImportConfirmationProgressRun Start(Guid sessionId, OctopusImportConfirmationProgressTracker tracker) => new Run(this, sessionId, tracker);

    // ── Write ────────────────────────────────────────────────────────────────────────────

    // Each write on its own scope: its own DbContext and connection, so it lands at once instead of inside the import's open transaction.
    private async Task<bool> WriteAsync(Guid sessionId, Guid attemptId, string progressJson, CancellationToken ct)
    {
        try
        {
            await using var scope = _lifetimeScope.BeginLifetimeScope();

            await scope.Resolve<IOctopusImportSessionDataProvider>().WriteConfirmationProgressAsync(sessionId, attemptId, progressJson, ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warning(ex, "Octopus import confirmation {SessionId} progress write failed; the import continues", sessionId);

            return false;
        }
    }

    // ── Run ──────────────────────────────────────────────────────────────────────────────

    private sealed class Run : IOctopusImportConfirmationProgressRun
    {
        private readonly OctopusImportConfirmationProgress _owner;
        private readonly Guid _sessionId;
        private readonly OctopusImportConfirmationProgressTracker _tracker;
        private readonly SemaphoreSlim _writing = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private long _written = -1;

        public Run(OctopusImportConfirmationProgress owner, Guid sessionId, OctopusImportConfirmationProgressTracker tracker)
        {
            (_owner, _sessionId, _tracker) = (owner, sessionId, tracker);
            _loop = LoopAsync(_stop.Token);
        }

        public Task FlushAsync() => FlushAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await StopLoopAsync().ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(FinalFlushTimeout);

            await FlushAsync(timeout.Token).ConfigureAwait(false);

            _stop.Dispose();
        }

        private async Task StopLoopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_owner._options.ProgressInterval, _owner._time);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await FlushAsync(ct).ConfigureAwait(false);
        }

        // One write at a time, and only of a snapshot newer than the last one written: an unchanged progress costs nothing.
        private async Task FlushAsync(CancellationToken ct)
        {
            try
            {
                await _writing.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await WriteIfChangedAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _writing.Release();
            }
        }

        private async Task WriteIfChangedAsync(CancellationToken ct)
        {
            var (version, progress) = _tracker.Snapshot();

            if (version == _written) return;

            if (await _owner.WriteAsync(_sessionId, progress.AttemptId, OctopusImportSessionService.SerializeProgress(progress), ct).ConfigureAwait(false)) _written = version;
        }
    }
}
