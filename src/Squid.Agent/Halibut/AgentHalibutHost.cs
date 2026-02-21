using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Agent.Configuration;
using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Agent.Halibut;

public class AgentHalibutHost : IAsyncDisposable
{
    private readonly HalibutRuntime _runtime;
    private readonly AgentSettings _settings;

    public AgentHalibutHost(
        X509Certificate2 agentCert,
        IScriptService scriptService,
        AgentSettings settings)
    {
        _settings = settings;

        var asyncAdapter = new AsyncScriptServiceAdapter(scriptService);

        var serviceFactory = new DelegateServiceFactory();
        serviceFactory.Register<IScriptService, IAsyncScriptService>(() => asyncAdapter);

        _runtime = new HalibutRuntimeBuilder()
            .WithServiceFactory(serviceFactory)
            .WithServerCertificate(agentCert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();
    }

    public void StartPolling(string serverThumbprint, string subscriptionId)
    {
        _runtime.Trust(serverThumbprint);

        var pollUri = new Uri($"poll://{subscriptionId}/");
        var serverEndpoint = new ServiceEndPoint(
            new Uri($"https://localhost:{_settings.ServerPollingPort}/"),
            serverThumbprint,
            HalibutTimeoutsAndLimits.RecommendedValues());

        // Use the actual server URL host for the endpoint
        var serverUri = new Uri(_settings.ServerUrl);
        var pollingEndpointUri = new Uri($"https://{serverUri.Host}:{_settings.ServerPollingPort}/");
        serverEndpoint = new ServiceEndPoint(
            pollingEndpointUri,
            serverThumbprint,
            HalibutTimeoutsAndLimits.RecommendedValues());

        _runtime.Poll(pollUri, serverEndpoint, CancellationToken.None);

        Log.Information("Halibut polling started. SubscriptionId={SubscriptionId}, ServerEndpoint={ServerEndpoint}",
            subscriptionId, pollingEndpointUri);
    }

    public async ValueTask DisposeAsync()
    {
        await _runtime.DisposeAsync().ConfigureAwait(false);
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
