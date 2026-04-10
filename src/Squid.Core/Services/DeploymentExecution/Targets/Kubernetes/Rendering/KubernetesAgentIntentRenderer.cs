using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Phase-5 pass-through renderer for the KubernetesAgent transport (Halibut polling to
/// a tentacle pod). Returns <see cref="IntentRenderContext.LegacyRequest"/> unchanged.
/// Phase 9 replaces this with real rendering logic that packs <c>StartScriptCommand</c>
/// payloads for the agent side.
/// </summary>
public sealed class KubernetesAgentIntentRenderer : PassThroughIntentRendererBase
{
    public override CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
}
