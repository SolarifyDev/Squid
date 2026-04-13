using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Flavors.LinuxTentacle;

/// <summary>
/// Listening Tentacles are registered manually on the Server side (via UI/API),
/// not by the agent. This registrar returns a placeholder registration using
/// the pre-configured Server thumbprint from settings.
/// </summary>
public sealed class NoOpRegistrar : ITentacleRegistrar
{
    private readonly TentacleSettings _settings;

    public NoOpRegistrar(TentacleSettings settings)
    {
        _settings = settings;
    }

    public Task<TentacleRegistration> RegisterAsync(TentacleIdentity identity, CancellationToken ct)
    {
        Log.Information("Listening mode — skipping auto-registration. Machine must be added on Server manually");

        return Task.FromResult(new TentacleRegistration
        {
            MachineId = 0,
            ServerThumbprint = _settings.ServerCertificate,
            SubscriptionUri = string.Empty
        });
    }
}
