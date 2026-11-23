using Halibut;
using Squid.Core.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IHalibutClientFactory : IScopedDependency
{
    IAsyncScriptService CreateClient(ServiceEndPoint endpoint);

    IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint);

    /// <summary>
    /// separate file-transfer client for out-of-band file
    /// upload/download to a polling tentacle. Returned client routes through
    /// the agent's <see cref="IFileTransferService"/> registration.
    /// </summary>
    IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint);
}

/// <summary>
/// Opens the V2 script-service client. A separate interface (ISP) so the V1 factory contract, which every
/// existing implementer and test double already satisfies, is not widened: only code that dispatches over
/// V2 needs it, and it is reached only after the agent has ADVERTISED V2 — the call itself must never be
/// used to probe for support (an agent without V2 answers it with a plain transport exception).
/// </summary>
public interface IScriptServiceV2ClientFactory : IScopedDependency
{
    IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint);
}

public class HalibutClientFactory : IHalibutClientFactory, IScriptServiceV2ClientFactory
{
    private readonly HalibutRuntime _halibutRuntime;

    public HalibutClientFactory(HalibutRuntime halibutRuntime)
        => _halibutRuntime = halibutRuntime;

    public IAsyncScriptService CreateClient(ServiceEndPoint endpoint)
        => _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);

    public IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint)
        => _halibutRuntime.CreateAsyncClient<IScriptServiceV2, IAsyncScriptServiceV2>(endpoint);

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
