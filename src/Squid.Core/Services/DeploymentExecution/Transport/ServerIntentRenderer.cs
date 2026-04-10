using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Transport;

/// <summary>
/// Phase-5 pass-through renderer for the "server" transport (<see cref="CommunicationStyle.None"/>),
/// used when a step is executed locally on the Squid API worker (RunOnServer). Returns
/// <see cref="IntentRenderContext.LegacyRequest"/> unchanged. Phase 9 replaces this with
/// real rendering logic tailored to the local-process execution backend.
/// </summary>
public sealed class ServerIntentRenderer : PassThroughIntentRendererBase
{
    public override CommunicationStyle CommunicationStyle => CommunicationStyle.None;
}
