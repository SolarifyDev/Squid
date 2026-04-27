using Halibut;
using Squid.Core.Halibut;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IHalibutClientFactory : IScopedDependency
{
    IAsyncScriptService CreateClient(ServiceEndPoint endpoint);

    IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint);

    /// <summary>
    /// P1-Phase9b.3 — separate file-transfer client for out-of-band file
    /// upload/download to a polling tentacle. Returned client routes through
    /// the agent's <see cref="IFileTransferService"/> registration.
    /// </summary>
    IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint);
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

    public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint)
        => _halibutRuntime.CreateAsyncClient<IFileTransferService, IAsyncClientFileTransferService>(endpoint);
}
