using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw.Rendering;

/// <summary>
/// Phase-5 pass-through renderer for the OpenClaw transport. Returns
/// <see cref="IntentRenderContext.LegacyRequest"/> unchanged. Phase 9 replaces
/// this with real rendering logic that builds OpenClaw HTTP API calls.
/// </summary>
public sealed class OpenClawIntentRenderer : PassThroughIntentRendererBase
{
    public override CommunicationStyle CommunicationStyle => CommunicationStyle.OpenClaw;
}
