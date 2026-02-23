using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Registration;

namespace Squid.Tentacle.Flavors.KubernetesAgent;

public sealed class KubernetesAgentRegistrar : ITentacleRegistrar
{
    private readonly TentacleRegistrationClient _client;

    public KubernetesAgentRegistrar(TentacleSettings tentacleSettings, KubernetesSettings kubernetesSettings)
    {
        _client = new TentacleRegistrationClient(tentacleSettings, kubernetesSettings);
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
