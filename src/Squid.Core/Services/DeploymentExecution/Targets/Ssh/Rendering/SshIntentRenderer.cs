using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Ssh.Rendering;

/// <summary>
/// Phase 9i — the SSH renderer no longer behaves as a pure pass-through.
///
/// <para>
/// When it sees a <see cref="RunScriptIntent"/>, it constructs a fresh
/// <see cref="ScriptExecutionRequest"/> from the intent plus
/// <see cref="IntentRenderContext"/>: script body, syntax, step/action framing, timeout,
/// variables, and target metadata come from the semantic inputs rather than the legacy
/// request. Package references and legacy side-files are still forwarded from
/// <see cref="IntentRenderContext.LegacyRequest"/> when present — that bridge goes away in
/// Phase 9j once handlers emit <c>Assets</c>/<c>Packages</c> on the intent directly.
/// </para>
///
/// <para>
/// For non-RunScript intents the renderer falls back to the Phase-5 pass-through path
/// (return <c>LegacyRequest</c> unchanged, throw <see cref="IntentRenderingException"/>
/// when it is absent). Phase 9j lands transport-native renderers for the remaining
/// intents and retires the fallback.
/// </para>
/// </summary>
public sealed class SshIntentRenderer : IIntentRenderer
{
    public CommunicationStyle CommunicationStyle => CommunicationStyle.Ssh;

    public bool CanRender(ExecutionIntent intent) => intent is not null;

    public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        return intent switch
        {
            RunScriptIntent runScript => Task.FromResult(RenderRunScript(runScript, context)),
            _ => Task.FromResult(FallbackToLegacy(intent, context))
        };
    }

    private static ScriptExecutionRequest RenderRunScript(RunScriptIntent intent, IntentRenderContext context)
    {
        var legacy = context.LegacyRequest;

        return new ScriptExecutionRequest
        {
            ScriptBody = intent.ScriptBody,
            Syntax = intent.Syntax,
            StepName = intent.StepName,
            ActionName = intent.ActionName,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Variables = context.EffectiveVariables.ToList(),
            Machine = context.Target.Machine,
            EndpointContext = context.Target.EndpointContext,
            ServerTaskId = context.ServerTaskId,
            ReleaseVersion = context.ReleaseVersion,
            Timeout = intent.Timeout ?? context.StepTimeout,
            Files = legacy?.Files ?? new Dictionary<string, byte[]>(),
            PackageReferences = legacy?.PackageReferences ?? new List<PackageAcquisitionResult>()
        };
    }

    private ScriptExecutionRequest FallbackToLegacy(ExecutionIntent intent, IntentRenderContext context)
    {
        if (context.LegacyRequest is null)
            throw new IntentRenderingException(
                CommunicationStyle,
                intent,
                "SshIntentRenderer has no native renderer for this intent and IntentRenderContext.LegacyRequest is not populated.");

        return context.LegacyRequest;
    }
}
