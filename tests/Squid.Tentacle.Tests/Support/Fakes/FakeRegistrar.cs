using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Tests.Support.Fakes;

public sealed class FakeRegistrar : ITentacleRegistrar
{
    public TentacleIdentity LastIdentity { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public int Calls { get; private set; }

    public TentacleRegistration Result { get; set; } = new()
    {
        MachineId = 1,
        ServerThumbprint = "server-thumbprint",
        SubscriptionUri = "poll://sub-id/"
    };

    public Task<TentacleRegistration> RegisterAsync(TentacleIdentity identity, CancellationToken ct)
    {
        Calls++;
        LastIdentity = identity;
        LastCancellationToken = ct;
        return Task.FromResult(Result);
    }
}
