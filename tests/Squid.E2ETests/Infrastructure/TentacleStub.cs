using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Services.Tentacle;

namespace Squid.E2ETests.Infrastructure;

public partial class TentacleStub : IAsyncDisposable
{
    public string Thumbprint { get; }
    public string SubscriptionId { get; }

    private readonly HalibutRuntime _agentRuntime;

    public TentacleStub(string serverThumbprint, int serverPollingPort, string kubeconfigPath)
    {
        SubscriptionId = Guid.NewGuid().ToString("N");

        var agentCert = CreateSelfSignedCert();
        Thumbprint = agentCert.Thumbprint;

        var scriptRunner = new ScriptRunner(kubeconfigPath);
        var asyncAdapter = new AsyncScriptServiceAdapter(scriptRunner);

        var serviceFactory = new DelegateServiceFactory();
        serviceFactory.Register<IScriptService, IAsyncScriptService>(() => asyncAdapter);

        _agentRuntime = new HalibutRuntimeBuilder()
            .WithServiceFactory(serviceFactory)
            .WithServerCertificate(agentCert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();

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

    public async ValueTask DisposeAsync()
    {
        await _agentRuntime.DisposeAsync().ConfigureAwait(false);
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

#pragma warning disable SYSLIB0057
        return new X509Certificate2(
            cert.Export(X509ContentType.Pfx, string.Empty),
            string.Empty,
            X509KeyStorageFlags.MachineKeySet);
#pragma warning restore SYSLIB0057
    }

    private sealed class AsyncScriptServiceAdapter : IAsyncScriptService
    {
        private readonly IScriptService _inner;

        public AsyncScriptServiceAdapter(IScriptService inner) => _inner = inner;

        public Task<ScriptTicket> StartScriptAsync(StartScriptCommand command)
            => Task.FromResult(_inner.StartScript(command));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
            => Task.FromResult(_inner.GetStatus(request));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
            => Task.FromResult(_inner.CompleteScript(command));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
            => Task.FromResult(_inner.CancelScript(command));
    }
}
