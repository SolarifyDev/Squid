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
        TentacleSettings settings)
    {
        _settings = settings;

        var asyncAdapter = new AsyncScriptServiceAdapter(scriptService);

        var serviceFactory = new DelegateServiceFactory();
        serviceFactory.Register<IScriptService, IScriptServiceAsync>(() => asyncAdapter);

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
        var pollingEndpointUri = new Uri(_settings.ServerCommsUrl);

        var serverEndpoint = new ServiceEndPoint(
            pollingEndpointUri,
            serverThumbprint,
            HalibutTimeoutsAndLimits.RecommendedValues());

        _runtime.Poll(pollUri, serverEndpoint, CancellationToken.None);

        Log.Information(
            "Halibut polling started. SubscriptionId={SubscriptionId}, SubscriptionUri={SubscriptionUri}, ServerEndpoint={ServerEndpoint}",
            subscriptionId, pollUri, pollingEndpointUri);
    }

    public void StartListening(int port)
    {
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
}
