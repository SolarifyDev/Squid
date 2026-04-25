namespace Squid.Tentacle.Halibut;

public interface ITentacleHalibutHost : IAsyncDisposable
{
    void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null);
    void StartListening(int port, string serverThumbprint = null);

    /// <summary>
    /// P0-T.4 (2026-04-24 audit): signals the Halibut polling loop to stop picking
    /// up new RPCs. Called during shutdown BEFORE the script-backend drain so the
    /// agent doesn't accept new scripts while waiting for in-flight ones to finish.
    /// Idempotent — safe to call multiple times. No-op if <see cref="StartPolling"/>
    /// was never invoked.
    ///
    /// <para>Pre-fix <c>StartPolling</c> passed <c>CancellationToken.None</c> to
    /// <c>HalibutRuntime.Poll</c>, so the only way to stop the poll was to dispose
    /// the runtime — which races with in-flight RPCs and can drop their responses.</para>
    /// </summary>
    void CancelPolling();

    /// <summary>
    /// True once <see cref="StartListening"/> has bound to a port. Used by the
    /// readiness probe so <c>/health/readyz</c> doesn't return 200 for a
    /// Listening Tentacle whose Halibut listener silently failed to bind
    /// (e.g. port 10933 already in use). Always false for Polling tentacles.
    /// </summary>
    bool IsListening { get; }

    /// <summary>
    /// The port the Halibut listener actually bound to. <c>0</c> when not
    /// listening. Useful for diagnostics when <c>port = 0</c> was passed to
    /// <see cref="StartListening"/> (kernel-assigned ephemeral port).
    /// </summary>
    int ListeningPort { get; }
}
