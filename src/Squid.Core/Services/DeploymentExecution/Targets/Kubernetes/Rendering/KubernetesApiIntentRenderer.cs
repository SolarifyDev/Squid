using System.Text;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
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
/// script body through unchanged.
/// </para>
///
/// <para>
/// Phase 9j.2 — <see cref="KubernetesApplyIntent"/> is also rendered natively. The
/// renderer synthesises the <c>kubectl apply -f</c> pipeline (one invocation per
/// file, server-side-apply flags from the intent), appends a
/// <see cref="KubernetesResourceWaitBuilder"/> status-check block when
/// <see cref="KubernetesApplyIntent.ObjectStatusCheck"/> is set, and wraps the resulting
/// script with the cluster's kubectl context for shell syntaxes. <c>Files</c> on the
/// returned request are derived directly from <see cref="KubernetesApplyIntent.YamlFiles"/>
/// — the legacy request is no longer consulted for YAML content.
/// </para>
///
/// <para>
/// For intents the renderer doesn't know how to render natively yet, it falls back to
/// the Phase-5 pass-through path (return <c>LegacyRequest</c> unchanged, throw
/// <see cref="IntentRenderingException"/> when it is absent). Later Phase 9j sub-steps
/// land native rendering for <see cref="KubernetesKustomizeIntent"/>,
/// <see cref="HelmUpgradeIntent"/>, and <see cref="HealthCheckIntent"/> and retire the
/// fallback.
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
            KubernetesApplyIntent apply => Task.FromResult(RenderKubernetesApply(apply, context)),
            _ => Task.FromResult(FallbackToLegacy(intent, context))
        };
    }

    private ScriptExecutionRequest RenderRunScript(RunScriptIntent intent, IntentRenderContext context)
    {
        var legacy = context.LegacyRequest;
        var wrappedBody = WrapRunScriptBody(intent, context);

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

    private string WrapRunScriptBody(RunScriptIntent intent, IntentRenderContext context)
    {
        if (!ScriptSyntaxHelper.IsShellSyntax(intent.Syntax))
            return intent.ScriptBody;

        return WrapWithKubectlContext(intent.ScriptBody, intent.Syntax, context);
    }

    private ScriptExecutionRequest RenderKubernetesApply(KubernetesApplyIntent intent, IntentRenderContext context)
    {
        var legacy = context.LegacyRequest;
        var files = ToLegacyFiles(intent.YamlFiles);
        var applyScript = BuildApplyScript(intent);
        var waitScript = KubernetesResourceWaitBuilder.BuildWaitScript(
            files, intent.ObjectStatusCheck, intent.StatusCheckTimeoutSeconds, intent.Namespace, intent.Syntax);
        var rawScript = applyScript + waitScript;
        var wrappedScript = WrapApplyBody(rawScript, intent, context);

        return new ScriptExecutionRequest
        {
            ScriptBody = wrappedScript,
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
            Files = files,
            PackageReferences = legacy?.PackageReferences ?? new List<PackageAcquisitionResult>()
        };
    }

    private static string BuildApplyScript(KubernetesApplyIntent intent)
    {
        if (intent.YamlFiles.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        var sortedFiles = intent.YamlFiles
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        foreach (var file in sortedFiles)
        {
            var targetPath = ToTargetPath(file.RelativePath, intent.Syntax);
            var cmd = KubernetesApplyCommandBuilder.Build(targetPath, intent.ServerSideApply, intent.FieldManager, intent.ForceConflicts);
            sb.AppendLine(cmd);
        }

        return sb.ToString();
    }

    private static string ToTargetPath(string relativePath, ScriptSyntax syntax)
    {
        var prefixed = $"./{relativePath}";

        return syntax == ScriptSyntax.Bash ? prefixed : prefixed.Replace("/", "\\");
    }

    private static Dictionary<string, byte[]> ToLegacyFiles(IReadOnlyList<DeploymentFile> yamlFiles)
    {
        var result = new Dictionary<string, byte[]>(yamlFiles.Count);

        foreach (var file in yamlFiles)
            result[file.RelativePath] = file.Content;

        return result;
    }

    private string WrapApplyBody(string scriptBody, KubernetesApplyIntent intent, IntentRenderContext context)
    {
        if (!ScriptSyntaxHelper.IsShellSyntax(intent.Syntax))
            return scriptBody;

        return WrapWithKubectlContext(scriptBody, intent.Syntax, context);
    }

    private string WrapWithKubectlContext(string scriptBody, ScriptSyntax syntax, IntentRenderContext context)
    {
        var scriptContext = new ScriptContext
        {
            Endpoint = context.Target.EndpointContext,
            Syntax = syntax,
            Variables = context.EffectiveVariables.ToList()
        };

        var customKubectl = context.EffectiveVariables
            .FirstOrDefault(v => string.Equals(v.Name, SpecialVariables.Kubernetes.CustomKubectlExecutable, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return _contextScriptBuilder.WrapWithContext(scriptBody, scriptContext, customKubectl);
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
