namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Proves a running confirmation is alive: while the returned handle is held, the session's heartbeat is refreshed every interval, so
/// the recovery sweep can tell a long import from one whose server died.
/// </summary>
public interface IOctopusImportConfirmationHeartbeat : IScopedDependency
{
    IAsyncDisposable Start(Guid sessionId, Guid attemptId);
}

public sealed class OctopusImportConfirmationHeartbeat : IOctopusImportConfirmationHeartbeat
{
    private readonly ILifetimeScope _lifetimeScope;
    private readonly TimeProvider _time;
    private readonly OctopusImportConfirmationResilienceOptions _options;

    public OctopusImportConfirmationHeartbeat(ILifetimeScope lifetimeScope) : this(lifetimeScope, TimeProvider.System, OctopusImportConfirmationResilienceOptions.FromEnvironment())
    {
    }

    internal OctopusImportConfirmationHeartbeat(ILifetimeScope lifetimeScope, TimeProvider time, OctopusImportConfirmationResilienceOptions options)
    {
        _lifetimeScope = lifetimeScope;
        _time = time;
        _options = options;
    }

    public IAsyncDisposable Start(Guid sessionId, Guid attemptId) => new Beat(this, sessionId, attemptId);

    // Each beat on its own scope: its own DbContext and connection, so it lands at once instead of inside the import's open transaction.
    private async Task TouchAsync(Guid sessionId, Guid attemptId, CancellationToken ct)
    {
        try
        {
            await using var scope = _lifetimeScope.BeginLifetimeScope();

            await scope.Resolve<IOctopusImportSessionDataProvider>().TouchConfirmationHeartbeatAsync(sessionId, attemptId, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Octopus import confirmation {SessionId} heartbeat failed; the import continues", sessionId);
        }
    }

    private sealed class Beat : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Beat(OctopusImportConfirmationHeartbeat heartbeat, Guid sessionId, Guid attemptId) => _loop = LoopAsync(heartbeat, sessionId, attemptId, _stop.Token);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _stop.Dispose();
        }

        private static async Task LoopAsync(OctopusImportConfirmationHeartbeat heartbeat, Guid sessionId, Guid attemptId, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(heartbeat._options.HeartbeatInterval, heartbeat._time);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await heartbeat.TouchAsync(sessionId, attemptId, ct).ConfigureAwait(false);
        }
    }
}
