using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Phase 9j.1 — the KubernetesApi renderer no longer behaves as a pure pass-through.
///
/// <para>
/// When it sees a <see cref="RunScriptIntent"/>, it constructs a fresh
/// <see cref="ScriptExecutionRequest"/> from the intent plus <see cref="IntentRenderContext"/>,
/// applying kubectl context wrapping via <see cref="IKubernetesApiContextScriptBuilder"/>
/// for shell syntaxes (bash / PowerShell). Non-shell syntaxes (Python, ...) pass the
/// script body through unchanged. Package references and legacy side-files are still
/// forwarded from <see cref="IntentRenderContext.LegacyRequest"/> when present — that
/// bridge goes away in Phase 9k once handlers emit <c>Assets</c>/<c>Packages</c> on the
/// intent directly.
/// </para>
///
/// <para>
/// For non-RunScript intents the renderer falls back to the Phase-5 pass-through path
/// (return <c>LegacyRequest</c> unchanged, throw <see cref="IntentRenderingException"/>
/// when it is absent). Phase 9j.2 lands native rendering for <see cref="KubernetesApplyIntent"/>,
/// <see cref="KubernetesKustomizeIntent"/>, <see cref="HelmUpgradeIntent"/>, and
/// <see cref="HealthCheckIntent"/> and retires the fallback.
/// </para>
/// </summary>
public sealed class KubernetesApiIntentRenderer : IIntentRenderer
{
    private readonly IKubernetesApiContextScriptBuilder _contextScriptBuilder;

    public KubernetesApiIntentRenderer(IKubernetesApiContextScriptBuilder contextScriptBuilder)
    {
        _contextScriptBuilder = contextScriptBuilder;
    }

    public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesApi;

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

    private ScriptExecutionRequest RenderRunScript(RunScriptIntent intent, IntentRenderContext context)
    {
        var legacy = context.LegacyRequest;
        var wrappedBody = WrapScriptBody(intent, context);

        return new ScriptExecutionRequest
        {
            ScriptBody = wrappedBody,
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

    private string WrapScriptBody(RunScriptIntent intent, IntentRenderContext context)
    {
        if (!ScriptSyntaxHelper.IsShellSyntax(intent.Syntax))
            return intent.ScriptBody;

        var scriptContext = new ScriptContext
        {
            Endpoint = context.Target.EndpointContext,
            Syntax = intent.Syntax,
            Variables = context.EffectiveVariables.ToList()
        };

        var customKubectl = context.EffectiveVariables
            .FirstOrDefault(v => string.Equals(v.Name, SpecialVariables.Kubernetes.CustomKubectlExecutable, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return _contextScriptBuilder.WrapWithContext(intent.ScriptBody, scriptContext, customKubectl);
    }

    private ScriptExecutionRequest FallbackToLegacy(ExecutionIntent intent, IntentRenderContext context)
    {
        if (context.LegacyRequest is null)
            throw new IntentRenderingException(
                CommunicationStyle,
                intent,
                "KubernetesApiIntentRenderer has no native renderer for this intent and IntentRenderContext.LegacyRequest is not populated.");

        return context.LegacyRequest;
    }
}
