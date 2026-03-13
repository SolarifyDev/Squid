using Halibut;

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
        => _halibutRuntime.CreateAsyncClient<ICapabilitiesService, IAsyncCapabilitiesService>(endpoint);
}
