using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Ssh.Rendering;

/// <summary>
/// Phase-5 pass-through renderer for the SSH transport. Returns
/// <see cref="IntentRenderContext.LegacyRequest"/> unchanged. Phase 9 replaces
/// this with real rendering logic that builds bash/PowerShell/Python scripts,
/// stages files via <c>SshFileTransfer</c>, and injects the runtime bundle.
/// </summary>
public sealed class SshIntentRenderer : PassThroughIntentRendererBase
{
    public override CommunicationStyle CommunicationStyle => CommunicationStyle.Ssh;
}
