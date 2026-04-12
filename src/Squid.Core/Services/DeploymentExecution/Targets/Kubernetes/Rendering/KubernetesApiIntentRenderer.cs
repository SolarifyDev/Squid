using System.Text;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
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
/// Natively renders all Kubernetes intent types for the <c>KubernetesApi</c> transport.
/// Each intent is translated into a <see cref="ScriptExecutionRequest"/> from the intent
/// plus <see cref="IntentRenderContext"/>, with kubectl context wrapping via
/// <see cref="IKubernetesApiContextScriptBuilder"/> for shell syntaxes.
///
/// <para>Supported intents: <see cref="RunScriptIntent"/>, <see cref="KubernetesApplyIntent"/>,
/// <see cref="HelmUpgradeIntent"/>, <see cref="KubernetesKustomizeIntent"/>.</para>
///
/// <para>Unsupported intents throw <see cref="IntentRenderingException"/>.</para>
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
            HelmUpgradeIntent helm => Task.FromResult(RenderHelmUpgrade(helm, context)),
            KubernetesKustomizeIntent kustomize => Task.FromResult(RenderKustomize(kustomize, context)),
            _ => throw new IntentRenderingException(CommunicationStyle, intent, $"KubernetesApiIntentRenderer has no native renderer for intent '{intent.Name}' ({intent.GetType().Name}).")
        };
    }

    private ScriptExecutionRequest RenderRunScript(RunScriptIntent intent, IntentRenderContext context)
    {
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
            TargetNamespace = context.TargetNamespace,
            PackageReferences = context.PackageReferences.ToList()
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
        var deploymentFiles = new DeploymentFileCollection(intent.YamlFiles);
        var applyScript = BuildApplyScript(intent);
        var waitScript = KubernetesResourceWaitBuilder.BuildWaitScript(
            deploymentFiles.ToLegacyDictionary(), intent.ObjectStatusCheck, intent.StatusCheckTimeoutSeconds, intent.Namespace, intent.Syntax);
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
            TargetNamespace = context.TargetNamespace,
            DeploymentFiles = deploymentFiles,
            PackageReferences = context.PackageReferences.ToList()
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

    private ScriptExecutionRequest RenderHelmUpgrade(HelmUpgradeIntent intent, IntentRenderContext context)
    {
        var deploymentFiles = HelmUpgradeScriptBuilder.BuildDeploymentFiles(intent);
        var rawScript = HelmUpgradeScriptBuilder.Build(intent, intent.Syntax);
        var wrappedScript = WrapShellBody(rawScript, intent.Syntax, context);

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
            Timeout = ((ExecutionIntent)intent).Timeout ?? context.StepTimeout,
            TargetNamespace = context.TargetNamespace,
            DeploymentFiles = deploymentFiles,
            PackageReferences = context.PackageReferences.ToList()
        };
    }

    private ScriptExecutionRequest RenderKustomize(KubernetesKustomizeIntent intent, IntentRenderContext context)
    {
        var rawScript = KubernetesKustomizeScriptBuilder.Build(intent, intent.Syntax);
        var wrappedScript = WrapShellBody(rawScript, intent.Syntax, context);

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
            TargetNamespace = context.TargetNamespace,
            PackageReferences = context.PackageReferences.ToList()
        };
    }

    private string WrapApplyBody(string scriptBody, KubernetesApplyIntent intent, IntentRenderContext context)
    {
        if (!ScriptSyntaxHelper.IsShellSyntax(intent.Syntax))
            return scriptBody;

        return WrapWithKubectlContext(scriptBody, intent.Syntax, context);
    }

    private string WrapShellBody(string scriptBody, ScriptSyntax syntax, IntentRenderContext context)
    {
        if (!ScriptSyntaxHelper.IsShellSyntax(syntax))
            return scriptBody;

        return WrapWithKubectlContext(scriptBody, syntax, context);
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

}
