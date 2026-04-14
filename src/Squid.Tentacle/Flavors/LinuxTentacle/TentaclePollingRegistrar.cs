using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Registration;

namespace Squid.Tentacle.Flavors.LinuxTentacle;

public sealed class TentaclePollingRegistrar : ITentacleRegistrar
{
    private readonly TentacleRegistrationClient _client;

    public TentaclePollingRegistrar(TentacleSettings tentacleSettings)
    {
        _client = new TentacleRegistrationClient(
            tentacleSettings, "/api/machines/register/tentacle-polling");
    }

    public async Task<TentacleRegistration> RegisterAsync(TentacleIdentity identity, CancellationToken ct)
    {
        var result = await _client.RegisterAsync(identity.SubscriptionId, identity.Thumbprint, ct).ConfigureAwait(false);

        return new TentacleRegistration
        {
            MachineId = result.MachineId,
            ServerThumbprint = result.ServerThumbprint,
            SubscriptionUri = result.SubscriptionUri
        };
    }
}
