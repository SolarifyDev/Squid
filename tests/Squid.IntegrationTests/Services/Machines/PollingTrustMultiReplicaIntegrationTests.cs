using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Squid.Core.Halibut;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.Halibut;
using Squid.Message.Commands.Machine;

namespace Squid.IntegrationTests.Services.Machines;

/// <summary>
/// Two server replicas over one real Postgres database. "Pod A" is the test container (its distributor is the one the registration and
/// delete services call); "pod B" is a second runtime with its own snapshot provider, distributor and refresher that only ever learns
/// about changes by reading the database. Fidelity: high for the DB query and the registration/delete services, the Halibut runtime is
/// real but no network connection is made (the E2E tier covers the connection).
/// </summary>
public class PollingTrustMultiReplicaIntegrationTests : TestBase
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    public PollingTrustMultiReplicaIntegrationTests() : base("PollingTrustMultiReplica", "squid_it_polling_trust_multi_replica")
    {
    }

    [Fact]
    public async Task AgentRegisteredThroughPodA_IsTrustedByPodB_AndUntrustedAfterDeleteThroughPodA()
    {
        await using var scopeB = CurrentScope.BeginLifetimeScope();
        using var podB = new ReplicaPod(scopeB, refreshIntervalSeconds: 1);

        var thumbprint = Guid.NewGuid().ToString("N").ToUpperInvariant();

        podB.Provider.IsTrusted(thumbprint).ShouldBeFalse();

        var machineId = await RegisterThroughPodAAsync(thumbprint).ConfigureAwait(false);

        await WaitAsync(() => podB.Provider.IsTrusted(thumbprint), $"pod B did not trust {thumbprint} within {Bound.TotalSeconds}s of it being registered through pod A; check the 'Halibut trust refresh' logs on pod B").ConfigureAwait(false);

        await Run<IMachineService>(async service => await service.DeleteMachinesAsync(new DeleteMachinesCommand { Ids = new List<int> { machineId } }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        await WaitAsync(() => !podB.Provider.IsTrusted(thumbprint), $"pod B still trusts {thumbprint} {Bound.TotalSeconds}s after the machine was deleted through pod A").ConfigureAwait(false);
    }

    private async Task<int> RegisterThroughPodAAsync(string thumbprint)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");

        var response = await Run<IMachineRegistrationService, RegisterMachineResponseData>(async service => await service.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
        {
            MachineName = $"trust-{subscriptionId[..8]}",
            Thumbprint = thumbprint,
            SubscriptionId = subscriptionId,
            SpaceId = 1,
            Roles = "web",
            AgentVersion = "1.0.0-test"
        }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        return response.MachineId;
    }

    private static async Task WaitAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + Bound;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure);

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private sealed class ReplicaPod : IDisposable
    {
        private readonly HalibutRuntime _runtime;
        private readonly PollingTrustRefresher _refresher;

        public SnapshotTrustProvider Provider { get; } = new();
        public PollingTrustDistributor Distributor { get; }

        public ReplicaPod(ILifetimeScope scope, int refreshIntervalSeconds)
        {
            _runtime = new HalibutRuntimeBuilder().WithServerCertificate(CreateCert()).WithTrustProvider(Provider).Build();

            var podScope = scope.BeginLifetimeScope(builder => builder.RegisterInstance(_runtime).As<HalibutRuntime>());

            Distributor = new PollingTrustDistributor(podScope);
            _refresher = new PollingTrustRefresher(Distributor, new TrustSettings { RefreshIntervalSeconds = refreshIntervalSeconds }, TimeProvider.System);

            _refresher.Start();
        }

        public void Dispose()
        {
            _refresher.Dispose();
            _runtime.Dispose();
        }

        private static X509Certificate2 CreateCert()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=pod-b", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty);
        }
    }
}
