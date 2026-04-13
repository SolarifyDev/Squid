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
        _runtime.Trust(serverThumbprint);

        var pollUri = ResolvePollUri(subscriptionId, subscriptionUri);
        var serverUrls = _settings.GetServerCommsUrls();

        if (serverUrls.Count == 0)
            throw new InvalidOperationException("No server comms URLs configured. Set ServerCommsUrl or ServerCommsAddresses.");

        var connectionCount = Math.Max(1, _settings.PollingConnectionCount);
        var totalConnections = 0;

        foreach (var serverUrl in serverUrls)
        {
            var pollingEndpointUri = new Uri(serverUrl);

            WarnIfCommsUrlMatchesApiUrl(pollingEndpointUri);

            var serverEndpoint = new ServiceEndPoint(pollingEndpointUri, serverThumbprint, HalibutTimeoutsAndLimits.RecommendedValues());

            for (var i = 0; i < connectionCount; i++)
            {
                _runtime.Poll(pollUri, serverEndpoint, CancellationToken.None);
                totalConnections++;
            }
        }

        Log.Information(
            "Halibut polling started. SubscriptionId={SubscriptionId}, ServerUrls={ServerUrlCount}, ConnectionsPerServer={ConnectionCount}, TotalConnections={TotalConnections}",
            subscriptionId, serverUrls.Count, connectionCount, totalConnections);
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
        if (!string.IsNullOrWhiteSpace(serverThumbprint))
        {
            _runtime.Trust(serverThumbprint);
            Log.Information("Trusted server thumbprint {Thumbprint} for listening mode", serverThumbprint);
        }

        _runtime.Listen(port);

        Log.Information("Halibut listening on port {Port}", port);
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
