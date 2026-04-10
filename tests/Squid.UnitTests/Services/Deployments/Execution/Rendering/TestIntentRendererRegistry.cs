using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.OpenClaw.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Ssh.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Execution.Rendering;

/// <summary>
/// Shared helper for pipeline tests that instantiate <c>ExecuteStepsPhase</c>. Returns a
/// fully-populated pass-through <see cref="IntentRendererRegistry"/> covering every
/// <c>CommunicationStyle</c> the phase may dispatch to (SSH, KubernetesApi, KubernetesAgent,
/// OpenClaw, and <c>None</c> for server-local steps). Because Phase 5 renderers are pure
/// pass-throughs, the returned registry preserves the legacy <c>ScriptExecutionRequest</c>
/// unchanged — tests observe exactly the behaviour they did before the renderer layer was
/// introduced.
/// </summary>
public static class TestIntentRendererRegistry
{
    public static IIntentRendererRegistry Create()
    {
        var renderers = new IIntentRenderer[]
        {
            new SshIntentRenderer(),
            new KubernetesApiIntentRenderer(),
            new KubernetesAgentIntentRenderer(),
            new OpenClawIntentRenderer(),
            new ServerIntentRenderer()
        };

        return new IntentRendererRegistry(renderers);
    }
}
