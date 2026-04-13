using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Core;

namespace Squid.E2ETests.Infrastructure;

public partial class TentacleStub : IAsyncDisposable
{
    public string Thumbprint { get; }
    public string SubscriptionId { get; }
    public int? ListeningPort { get; }

    private readonly HalibutRuntime _agentRuntime;

    /// <summary>Creates a Polling-mode TentacleStub that connects outbound to the Server's polling listener.</summary>
    public static TentacleStub CreatePolling(string serverThumbprint, int serverPollingPort, string kubeconfigPath = null)
        => new(serverThumbprint, serverPollingPort, kubeconfigPath);

    /// <summary>Creates a Listening-mode TentacleStub that accepts inbound connections from the Server.</summary>
    public static TentacleStub CreateListening(int listeningPort, string kubeconfigPath = null)
        => new(listeningPort, kubeconfigPath);

    // Polling mode constructor
    private TentacleStub(string serverThumbprint, int serverPollingPort, string kubeconfigPath)
    {
        SubscriptionId = Guid.NewGuid().ToString("N");

        var agentCert = CreateSelfSignedCert();
        Thumbprint = agentCert.Thumbprint;

        _agentRuntime = BuildRuntime(agentCert, kubeconfigPath);
        _agentRuntime.Trust(serverThumbprint);

        var timeouts = HalibutTimeoutsAndLimits.RecommendedValues();

        _agentRuntime.Poll(
            new Uri($"poll://{SubscriptionId}/"),
            new ServiceEndPoint(
                new Uri($"https://localhost:{serverPollingPort}/"),
                serverThumbprint,
                timeouts),
            CancellationToken.None);
    }

    // Listening mode constructor
    private TentacleStub(int listeningPort, string kubeconfigPath)
    {
        SubscriptionId = Guid.NewGuid().ToString("N");
        ListeningPort = listeningPort;

        var agentCert = CreateSelfSignedCert();
        Thumbprint = agentCert.Thumbprint;

        _agentRuntime = BuildRuntime(agentCert, kubeconfigPath);
        _agentRuntime.Listen(listeningPort);
    }

    /// <summary>Trust a Server certificate (required for Listening mode so the TLS handshake succeeds).</summary>
    public void Trust(string serverThumbprint)
    {
        _agentRuntime.Trust(serverThumbprint);
    }

    public async ValueTask DisposeAsync()
    {
        await _agentRuntime.DisposeAsync().ConfigureAwait(false);
    }

    private static HalibutRuntime BuildRuntime(X509Certificate2 agentCert, string kubeconfigPath)
    {
        var scriptRunner = new ScriptRunner(kubeconfigPath);
        var asyncAdapter = new AsyncScriptServiceAdapter(scriptRunner);

        var capsService = new CapabilitiesService();
        var asyncCapsAdapter = new AsyncCapabilitiesServiceAdapter(capsService);

        var serviceFactory = new DelegateServiceFactory();
        serviceFactory.Register<IScriptService, IScriptServiceAsync>(() => asyncAdapter);
        serviceFactory.Register<ICapabilitiesService, ICapabilitiesServiceAsync>(() => asyncCapsAdapter);

        return new HalibutRuntimeBuilder()
            .WithServiceFactory(serviceFactory)
            .WithServerCertificate(agentCert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=tentacle-stub-e2e",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, string.Empty),
            string.Empty,
            X509KeyStorageFlags.Exportable);
    }

    private sealed class AsyncCapabilitiesServiceAdapter : ICapabilitiesServiceAsync
    {
        private readonly ICapabilitiesService _inner;

        public AsyncCapabilitiesServiceAdapter(ICapabilitiesService inner) => _inner = inner;

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct)
            => Task.FromResult(_inner.GetCapabilities(request));
    }

    private sealed class AsyncScriptServiceAdapter : IScriptServiceAsync
    {
        private readonly IScriptService _inner;

        public AsyncScriptServiceAdapter(IScriptService inner) => _inner = inner;

        public Task<ScriptTicket> StartScriptAsync(StartScriptCommand command, CancellationToken ct)
            => Task.FromResult(_inner.StartScript(command));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct)
            => Task.FromResult(_inner.GetStatus(request));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct)
            => Task.FromResult(_inner.CompleteScript(command));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct)
            => Task.FromResult(_inner.CancelScript(command));
    }
}
