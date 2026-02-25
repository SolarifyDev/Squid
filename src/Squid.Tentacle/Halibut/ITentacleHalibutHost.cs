namespace Squid.Tentacle.Halibut;

public interface ITentacleHalibutHost : IAsyncDisposable
{
    void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null);
    void StartListening(int port);
}
