namespace Squid.Tentacle.Halibut;

public interface ITentacleHalibutHost : IAsyncDisposable
{
    void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null);
    void StartListening(int port, string serverThumbprint = null);

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
