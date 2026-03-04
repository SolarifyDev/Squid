namespace Squid.Tentacle.Health;

public interface IHealthCheckServer : IAsyncDisposable
{
    void Start();
}
