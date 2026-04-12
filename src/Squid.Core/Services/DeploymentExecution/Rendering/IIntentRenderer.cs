using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Rendering;

/// <summary>
/// A per-transport renderer that translates a semantic <see cref="ExecutionIntent"/> into a
/// concrete <see cref="ScriptExecutionRequest"/> ready for execution by the transport's
/// <c>IExecutionStrategy</c>.
///
/// <para>
/// Phase 5 establishes this abstraction as a pass-through layer in the pipeline:
/// the legacy <c>BuildScriptExecutionRequest</c> path builds the request, the adapter
/// wraps the corresponding intent, and the renderer returns the legacy request unchanged.
/// Phase 9 flips this so that handlers produce real intents and the renderer does the
/// actual translation work.
/// </para>
/// </summary>
public interface IIntentRenderer : IScopedDependency
{
    /// <summary>The transport this renderer serves.</summary>
    CommunicationStyle CommunicationStyle { get; }

    /// <summary>
    /// Whether this renderer can handle <paramref name="intent"/>. Used by
    /// <see cref="IIntentRendererRegistry"/> to resolve among multiple renderers
    /// registered for the same transport (future multi-intent-kind case).
    /// </summary>
    bool CanRender(ExecutionIntent intent);

    /// <summary>
    /// Translate <paramref name="intent"/> into a <see cref="ScriptExecutionRequest"/>
    /// in the given <paramref name="context"/>. Implementations throw
    /// <c>UnsupportedIntentException</c> for unknown intent kinds and
    /// <c>IntentRenderingException</c> for failures during translation.
    /// </summary>
    Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct);
}
