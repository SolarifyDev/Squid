using Halibut;
using Squid.Core.Halibut;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IHalibutClientFactory : IScopedDependency
{
    IAsyncScriptService CreateClient(ServiceEndPoint endpoint);

    IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint);
}

public class HalibutClientFactory : IHalibutClientFactory
{
    private readonly HalibutRuntime _halibutRuntime;

    public HalibutClientFactory(HalibutRuntime halibutRuntime)
        => _halibutRuntime = halibutRuntime;

    public IAsyncScriptService CreateClient(ServiceEndPoint endpoint)
        => _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);

    public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint)
    {
        // Wrap the raw Halibut proxy with the back-compat decorator so mixed-
        // version deployments (new server × old Tentacle that predates the
        // capabilities service) degrade gracefully to the pre-capabilities
        // contract rather than failing health checks outright.
        var raw = _halibutRuntime.CreateAsyncClient<ICapabilitiesService, IAsyncCapabilitiesService>(endpoint);
        return new BackwardsCompatibleCapabilitiesClient(raw);
    }
}
