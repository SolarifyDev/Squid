using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Rendering;

/// <summary>
/// Behaviour-preserving base for the four Phase-5 concrete renderers (SSH, KubernetesApi,
/// KubernetesAgent, OpenClaw). Every subclass forwards the pre-built
/// <see cref="IntentRenderContext.LegacyRequest"/> unchanged — so inserting the renderer
/// layer into the pipeline is a pure no-op. Phase 9 replaces these subclasses with real
/// per-transport rendering logic.
/// </summary>
public abstract class PassThroughIntentRendererBase : IIntentRenderer
{
    public abstract CommunicationStyle CommunicationStyle { get; }

    public virtual bool CanRender(ExecutionIntent intent) => intent is not null;

    public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
    {
        if (intent is null) throw new ArgumentNullException(nameof(intent));
        if (context is null) throw new ArgumentNullException(nameof(context));

        EnsureLegacyRequestPresent(intent, context);

        return Task.FromResult(context.LegacyRequest!);
    }

    private void EnsureLegacyRequestPresent(ExecutionIntent intent, IntentRenderContext context)
    {
        if (context.LegacyRequest is null)
            throw new IntentRenderingException(
                CommunicationStyle,
                intent,
                "Phase-5 pass-through renderer requires IntentRenderContext.LegacyRequest to be populated.");
    }
}
