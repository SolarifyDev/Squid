using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Rendering;

public sealed class TentacleListeningIntentRenderer : IIntentRenderer
{
    public CommunicationStyle CommunicationStyle => CommunicationStyle.LinuxListening;

    public bool CanRender(ExecutionIntent intent) => intent is RunScriptIntent;

    public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        if (intent is not RunScriptIntent runScript)
            throw new IntentRenderingException(CommunicationStyle, intent, $"TentacleListeningIntentRenderer only supports RunScriptIntent, got '{intent.GetType().Name}'.");

        return Task.FromResult(RenderRunScript(runScript, context));
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
