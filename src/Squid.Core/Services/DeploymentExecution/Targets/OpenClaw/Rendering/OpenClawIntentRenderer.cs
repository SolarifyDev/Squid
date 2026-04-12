using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw.Rendering;

/// <summary>
/// Natively renders <see cref="OpenClawInvokeIntent"/> for the <c>OpenClaw</c> transport.
/// <see cref="OpenClawInvokeIntent.Kind"/> is mapped to the legacy action type string and
/// <see cref="OpenClawInvokeIntent.Parameters"/> are copied into action properties.
///
/// <para>Unsupported intents throw <see cref="IntentRenderingException"/>.</para>
/// </summary>
public sealed class OpenClawIntentRenderer : IIntentRenderer
{
    public CommunicationStyle CommunicationStyle => CommunicationStyle.OpenClaw;

    public bool CanRender(ExecutionIntent intent) => intent is not null;

    public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        return intent switch
        {
            OpenClawInvokeIntent invoke => Task.FromResult(RenderInvoke(invoke, context)),
            _ => throw new IntentRenderingException(CommunicationStyle, intent, $"OpenClawIntentRenderer has no native renderer for intent '{intent.Name}' ({intent.GetType().Name}).")
        };
    }

    private static ScriptExecutionRequest RenderInvoke(OpenClawInvokeIntent intent, IntentRenderContext context)
    {
        return new ScriptExecutionRequest
        {
            ActionType = MapKindToActionType(intent.Kind),
            ActionProperties = intent.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            ScriptBody = string.Empty,
            Syntax = ScriptSyntax.Bash,
            StepName = intent.StepName,
            ActionName = intent.ActionName,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Variables = context.EffectiveVariables.ToList(),
            Machine = context.Target.Machine,
            EndpointContext = context.Target.EndpointContext,
            ServerTaskId = context.ServerTaskId,
            ReleaseVersion = context.ReleaseVersion,
            Timeout = intent.Timeout ?? context.StepTimeout,
            Files = new Dictionary<string, byte[]>(),
            PackageReferences = new List<PackageAcquisitionResult>()
        };
    }

    private static string MapKindToActionType(OpenClawInvocationKind kind) => kind switch
    {
        OpenClawInvocationKind.Wake => SpecialVariables.ActionTypes.OpenClawWake,
        OpenClawInvocationKind.Assert => SpecialVariables.ActionTypes.OpenClawAssert,
        OpenClawInvocationKind.ChatCompletion => SpecialVariables.ActionTypes.OpenClawChatCompletion,
        OpenClawInvocationKind.FetchResult => SpecialVariables.ActionTypes.OpenClawFetchResult,
        OpenClawInvocationKind.InvokeTool => SpecialVariables.ActionTypes.OpenClawInvokeTool,
        OpenClawInvocationKind.RunAgent => SpecialVariables.ActionTypes.OpenClawRunAgent,
        OpenClawInvocationKind.WaitSession => SpecialVariables.ActionTypes.OpenClawWaitSession,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unknown OpenClawInvocationKind: {kind}")
    };

}
