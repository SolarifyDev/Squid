using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Tentacle.Configuration;
using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.Halibut;

public class TentacleHalibutHost : ITentacleHalibutHost
{
    private readonly HalibutRuntime _runtime;
    private readonly TentacleSettings _settings;

    public bool IsListening { get; private set; }
    public int ListeningPort { get; private set; }

    public TentacleHalibutHost(
        X509Certificate2 tentacleCert,
        IScriptService scriptService,
        TentacleSettings settings,
        ICapabilitiesService capabilitiesService = null)
    {
        _settings = settings;

        var asyncAdapter = new AsyncScriptServiceAdapter(scriptService);
        var capsAdapter = new AsyncCapabilitiesServiceAdapter(capabilitiesService ?? new Core.CapabilitiesService());

        var serviceFactory = new DelegateServiceFactory();
        serviceFactory.Register<IScriptService, IScriptServiceAsync>(() => asyncAdapter);
        serviceFactory.Register<ICapabilitiesService, ICapabilitiesServiceAsync>(() => capsAdapter);

        _runtime = new HalibutRuntimeBuilder()
            .WithServiceFactory(serviceFactory)
            .WithServerCertificate(tentacleCert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();
    }

    public void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null)
    {
        // ServerCertificate may be a comma-separated list (Octopus-aligned multi-server trust).
        // Every listed thumbprint is Trust()ed so cert-rotation windows where old+new coexist
        // don't break running Tentacles.
        var trusted = Squid.Tentacle.Certificate.ServerCertificateValidator.ParseThumbprints(serverThumbprint);

        foreach (var thumbprint in trusted)
            _runtime.Trust(thumbprint);

        // Primary thumbprint (first in list) is used for the ServiceEndPoint TLS pinning.
        // Halibut's ServiceEndPoint only accepts one thumbprint per endpoint, so if callers
        // want true multi-server trust they must also configure multiple ServerCommsAddresses.
        var primaryThumbprint = trusted.Count > 0 ? trusted[0] : serverThumbprint;

        var pollUri = ResolvePollUri(subscriptionId, subscriptionUri);
        var serverUrls = _settings.GetServerCommsUrls();

        if (serverUrls.Count == 0)
            throw new InvalidOperationException("No server comms URLs configured. Set ServerCommsUrl or ServerCommsAddresses.");

        var connectionCount = Math.Max(1, _settings.PollingConnectionCount);
        var totalConnections = 0;
        var halibutProxy = ProxyConfigurationBuilder.BuildHalibutProxy(_settings.Proxy);

        foreach (var serverUrl in serverUrls)
        {
            var pollingEndpointUri = new Uri(serverUrl);

            WarnIfCommsUrlMatchesApiUrl(pollingEndpointUri);

            var serverEndpoint = new ServiceEndPoint(
                pollingEndpointUri,
                primaryThumbprint,
                halibutProxy,                                           // null = direct connection
                HalibutTimeoutsAndLimits.RecommendedValues());

            for (var i = 0; i < connectionCount; i++)
            {
                _runtime.Poll(pollUri, serverEndpoint, CancellationToken.None);
                totalConnections++;
            }
        }

        Log.Information(
            "Halibut polling started. SubscriptionId={SubscriptionId}, ServerUrls={ServerUrlCount}, ConnectionsPerServer={ConnectionCount}, TotalConnections={TotalConnections}, Proxy={Proxy}",
            subscriptionId, serverUrls.Count, connectionCount, totalConnections,
            halibutProxy == null ? "direct" : $"{_settings.Proxy.Host}:{_settings.Proxy.Port}");
    }

    private void WarnIfCommsUrlMatchesApiUrl(Uri commsUri)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl)) return;

        try
        {
            var apiUri = new Uri(_settings.ServerUrl);

            if (string.Equals(apiUri.Host, commsUri.Host, StringComparison.OrdinalIgnoreCase) &&
                apiUri.Port == commsUri.Port)
            {
                Log.Warning(
                    "ServerCommsUrl ({ServerCommsUrl}) has the same host:port as ServerUrl ({ServerUrl}). " +
                    "ServerCommsUrl should point to the Halibut polling port (default 10943), not the HTTP API port",
                    commsUri, apiUri);
            }
        }
        catch (UriFormatException)
        {
            // ServerUrl is invalid, skip comparison
        }
    }

    public void StartListening(int port, string serverThumbprint = null)
    {
        var trusted = Squid.Tentacle.Certificate.ServerCertificateValidator.ParseThumbprints(serverThumbprint);

        foreach (var thumbprint in trusted)
        {
            _runtime.Trust(thumbprint);
            Log.Information("Trusted server thumbprint {Thumbprint} for listening mode", thumbprint);
        }

        // HalibutRuntime.Listen returns the actual bound port — important when port=0
        // (kernel-assigned ephemeral) and useful even when explicit, because it confirms
        // the bind succeeded. The returned int is what we expose to /health/readyz.
        var boundPort = _runtime.Listen(port);

        ListeningPort = boundPort;
        IsListening = true;

        Log.Information("Halibut listening on port {Port} (trusted {Count} server thumbprint(s))", boundPort, trusted.Count);
    }

    public static Uri ResolvePollUri(string subscriptionId, string subscriptionUri)
    {
        return string.IsNullOrWhiteSpace(subscriptionUri)
            ? new Uri($"poll://{subscriptionId}/")
            : new Uri(subscriptionUri);
    }


    public async ValueTask DisposeAsync()
    {
        await _runtime.DisposeAsync().ConfigureAwait(false);
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

    private sealed class AsyncCapabilitiesServiceAdapter : ICapabilitiesServiceAsync
    {
        private readonly ICapabilitiesService _inner;

        public AsyncCapabilitiesServiceAdapter(ICapabilitiesService inner) => _inner = inner;

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct)
            => Task.FromResult(_inner.GetCapabilities(request));
    }
}
