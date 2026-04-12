using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Ssh.Rendering;

/// <summary>
/// Natively renders <see cref="RunScriptIntent"/> for the <c>Ssh</c> transport.
/// Constructs a <see cref="ScriptExecutionRequest"/> from the intent plus
/// <see cref="IntentRenderContext"/> — script body, syntax, step/action framing, timeout,
/// variables, and target metadata come from the semantic inputs.
///
/// <para>Unsupported intents throw <see cref="IntentRenderingException"/>.</para>
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
            _ => throw new IntentRenderingException(CommunicationStyle, intent, $"SshIntentRenderer has no native renderer for intent '{intent.Name}' ({intent.GetType().Name}).")
        };
    }

    private static ScriptExecutionRequest RenderRunScript(RunScriptIntent intent, IntentRenderContext context)
    {
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
            PackageReferences = context.PackageReferences.ToList()
        };
    }
}
