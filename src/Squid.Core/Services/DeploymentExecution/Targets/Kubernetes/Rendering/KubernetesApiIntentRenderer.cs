using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Phase-5 pass-through renderer for the KubernetesApi transport (local kubectl
/// execution on the worker). Returns <see cref="IntentRenderContext.LegacyRequest"/>
/// unchanged. Phase 9 replaces this with real rendering logic that assembles kubectl
/// context, writes YAML assets, and wraps scripts with the kubectl bootstrap.
/// </summary>
public sealed class KubernetesApiIntentRenderer : PassThroughIntentRendererBase
{
    public override CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesApi;
}
