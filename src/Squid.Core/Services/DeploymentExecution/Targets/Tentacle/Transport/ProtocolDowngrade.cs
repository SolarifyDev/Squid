using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// The one definition of "this V2 call failed because the agent was downgraded to V1", shared by every path that addresses a script over V2 (start,
/// resume probe, cancel). A V1-only agent answers a V2 call with a plain transport error the classifier treats as a blip, so a failure alone proves
/// nothing: the agent is asked what it advertises, and only an answer that no longer lists V2 licenses the V1 fall-back.
/// </summary>
internal static class ProtocolDowngrade
{
    /// <summary>A V2 call that failed in a way a V1-only agent would also produce: a transport blip, or the service/method being absent.</summary>
    internal static bool IsPossibleMismatch(ScriptProtocolVersion version, Exception ex)
        => version == ScriptProtocolVersion.V2 && (TransientFailureClassifier.IsTransient(ex) || TransientFailureClassifier.IsServiceMissing(ex));

    /// <summary>True only when the agent ANSWERED a capabilities call and no longer lists V2; an unanswered call (or no refresher) proves nothing.</summary>
    internal static async Task<bool> AgentWithdrewV2Async(IMachineCapabilitiesRefresher refresher, int machineId, ServiceEndPoint endpoint, CancellationToken ct)
    {
        if (refresher == null) return false;

        var capabilities = await refresher.RefreshFromAgentAsync(machineId, endpoint, ct).ConfigureAwait(false);

        return capabilities != null && !capabilities.SupportsScriptServiceV2;
    }
}
