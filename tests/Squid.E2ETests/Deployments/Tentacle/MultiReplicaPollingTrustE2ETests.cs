using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Halibut.Util;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Xunit;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// Fidelity: HIGH. Two full server containers (real <c>HalibutModule</c> wiring, real listeners on loopback ports, real Postgres) and a
/// real Halibut polling agent. The agent is registered through replica A's real registration service and then polls replica B, which
/// has never been told about it except through the database. Replica B's periodic refresh is an hour, so the only thing that can make B
/// accept the agent within the timeout is the unauthorized-connect refresh. The agent uses a 1-2 second reconnect policy (the default
/// is at least 5 seconds) only to keep the test short; server-side acceptance does not depend on it.
/// </summary>
[Trait("Category", "E2E")]
public class MultiReplicaPollingTrustE2ETests : IClassFixture<MultiReplicaPollingTrustE2EFixture<MultiReplicaPollingTrustE2ETests>>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly MultiReplicaPollingTrustE2EFixture<MultiReplicaPollingTrustE2ETests> _fixture;

    public MultiReplicaPollingTrustE2ETests(MultiReplicaPollingTrustE2EFixture<MultiReplicaPollingTrustE2ETests> fixture) => _fixture = fixture;

    [Fact]
    public async Task AgentRegisteredThroughReplicaA_ServesARequestQueuedOnReplicaB()
    {
        await using var agent = new PollingAgent();
        var subscriptionId = Guid.NewGuid().ToString("N");

        await RegisterThroughReplicaAAsync(agent.Thumbprint, subscriptionId).ConfigureAwait(false);

        agent.StartPolling(_fixture.ReplicaBPollingPort, _fixture.ReplicaBThumbprint, subscriptionId);

        var response = await CallCapabilitiesOnReplicaBAsync(agent.Thumbprint, subscriptionId).ConfigureAwait(false);

        response.AgentVersion.ShouldBe(PollingAgent.Version, "the request queued on replica B was not served by the agent that polls replica B");
    }

    private async Task RegisterThroughReplicaAAsync(string thumbprint, string subscriptionId)
    {
        await _fixture.Run<IMachineRegistrationService>(async service =>
        {
            await service.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
            {
                MachineName = $"replica-trust-{subscriptionId[..8]}",
                Thumbprint = thumbprint,
                SubscriptionId = subscriptionId,
                SpaceId = 1,
                Roles = "web",
                AgentVersion = "1.0.0-test"
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<CapabilitiesResponse> CallCapabilitiesOnReplicaBAsync(string agentThumbprint, string subscriptionId)
    {
        var endpoint = new ServiceEndPoint(new Uri($"poll://{subscriptionId}/"), agentThumbprint, HalibutTimeoutsAndLimits.RecommendedValues());

        using var cts = new CancellationTokenSource(Timeout);

        try
        {
            var client = _fixture.ReplicaB.Resolve<IHalibutClientFactory>().CreateCapabilitiesClient(endpoint);

            return await client.GetCapabilitiesAsync(new CapabilitiesRequest()).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Replica B did not serve a request for agent {agentThumbprint} within {Timeout.TotalSeconds}s. The agent was registered through replica A only; check replica B's logs for 'Unknown Halibut client' / 'Halibut trust reconfigured' and whether its polling listener is on port {_fixture.ReplicaBPollingPort}.");
        }
    }

    private sealed class PollingAgent : IAsyncDisposable
    {
        public const string Version = "9.9.9-e2e";

        private readonly HalibutRuntime _runtime;
        private readonly CancellationTokenSource _stop = new();

        public string Thumbprint { get; }

        public PollingAgent()
        {
            var cert = CreateCert();

            Thumbprint = cert.Thumbprint;

            var services = new DelegateServiceFactory();
            services.Register<ICapabilitiesService, ICapabilitiesServiceAsync>(() => new CapabilitiesStub());

            _runtime = new HalibutRuntimeBuilder()
                .WithServiceFactory(services)
                .WithServerCertificate(cert)
                .WithPollingReconnectRetryPolicy(() => new RetryPolicy(1.0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)))
                .Build();
        }

        public void StartPolling(int serverPort, string serverThumbprint, string subscriptionId)
        {
            _runtime.Trust(serverThumbprint);

            _runtime.Poll(new Uri($"poll://{subscriptionId}/"), new ServiceEndPoint(new Uri($"https://localhost:{serverPort}/"), serverThumbprint, HalibutTimeoutsAndLimits.RecommendedValues()), _stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();

            await _runtime.DisposeAsync().ConfigureAwait(false);

            _stop.Dispose();
        }

        private static X509Certificate2 CreateCert()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=replica-trust-agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
        }

        private sealed class CapabilitiesStub : ICapabilitiesServiceAsync
        {
            public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct) => Task.FromResult(new CapabilitiesResponse { AgentVersion = Version });
        }
    }
}
