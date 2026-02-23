namespace Squid.Tentacle.Abstractions;

public interface ITentacleRegistrar
{
    Task<TentacleRegistration> RegisterAsync(TentacleIdentity identity, CancellationToken ct);
}
