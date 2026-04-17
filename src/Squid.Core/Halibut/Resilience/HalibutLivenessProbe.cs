using Halibut;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// Liveness probe that talks to the agent through the existing Halibut
/// capabilities service. A successful GetCapabilities round-trip within the
/// supplied timeout means the agent is responsive; anything else (timeout,
/// transport exception) is treated as unreachable. The probe never throws —
/// callers get a boolean so they can implement their own consecutive-failure
/// policy without exception-driven control flow.
/// </summary>
public sealed class HalibutLivenessProbe : IAgentLivenessProbe, IScopedDependency
{
    private readonly IHalibutClientFactory _clientFactory;

    public HalibutLivenessProbe(IHalibutClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        if (endpoint == null) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var client = _clientFactory.CreateCapabilitiesClient(endpoint);
            var probeTask = client.GetCapabilitiesAsync(new CapabilitiesRequest());
            var timeoutTask = Task.Delay(timeout, cts.Token);

            var completed = await Task.WhenAny(probeTask, timeoutTask).ConfigureAwait(false);

            if (completed == timeoutTask) return false;

            await probeTask.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
