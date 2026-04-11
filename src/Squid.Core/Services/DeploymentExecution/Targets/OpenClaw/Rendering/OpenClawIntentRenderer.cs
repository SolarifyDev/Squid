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
/// Phase 9j.4 — the OpenClaw renderer no longer behaves as a pure pass-through.
///
/// <para>
/// When it sees an <see cref="OpenClawInvokeIntent"/>, it constructs a fresh
/// <see cref="ScriptExecutionRequest"/> from the intent plus <see cref="IntentRenderContext"/>.
/// <see cref="OpenClawInvokeIntent.Kind"/> is mapped to the legacy
/// <see cref="ScriptExecutionRequest.ActionType"/> string the
/// <see cref="Squid.Core.Services.DeploymentExecution.OpenClaw.Transport.OpenClawExecutionStrategy"/>
/// dispatches on, and <see cref="OpenClawInvokeIntent.Parameters"/> are copied verbatim
/// into <see cref="ScriptExecutionRequest.ActionProperties"/>. Variables, machine,
/// endpoint, server task id, release version and timeout are hydrated from the context —
/// the legacy request is no longer consulted for OpenClaw invocations.
/// </para>
///
/// <para>
/// For intents the renderer doesn't know how to render natively yet, it falls back to the
/// Phase-5 pass-through path (return <c>LegacyRequest</c> unchanged, throw
/// <see cref="IntentRenderingException"/> when it is absent).
/// </para>
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
            _ => Task.FromResult(FallbackToLegacy(intent, context))
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

    private ScriptExecutionRequest FallbackToLegacy(ExecutionIntent intent, IntentRenderContext context)
    {
        if (context.LegacyRequest is null)
            throw new IntentRenderingException(
                CommunicationStyle,
                intent,
                "OpenClawIntentRenderer has no native renderer for this intent and IntentRenderContext.LegacyRequest is not populated.");

        return context.LegacyRequest;
    }
}
