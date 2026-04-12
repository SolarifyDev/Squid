using System.Text;
using System.Text.RegularExpressions;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Natively renders all Kubernetes intent types for the <c>KubernetesAgent</c> transport.
/// Each intent is translated into a <see cref="ScriptExecutionRequest"/> from the intent
/// plus <see cref="IntentRenderContext"/>, with namespace preamble wrapping
/// (<c>kubectl config set-context</c> + optional namespace creation probe) for shell syntaxes.
///
/// <para>Supported intents: <see cref="RunScriptIntent"/>, <see cref="KubernetesApplyIntent"/>,
/// <see cref="HelmUpgradeIntent"/>, <see cref="KubernetesKustomizeIntent"/>.</para>
///
/// <para>Namespace resolution for <see cref="RunScriptIntent"/> reads
/// <c>SpecialVariables.Kubernetes.Namespace</c> from
/// <see cref="IntentRenderContext.EffectiveVariables"/>; other intents carry namespace directly.</para>
///
/// <para>Unsupported intents throw <see cref="IntentRenderingException"/>.</para>
/// </summary>
public sealed class KubernetesAgentIntentRenderer : IIntentRenderer
{
    private static readonly Regex ValidKubernetesNameRegex = new("^[a-z0-9][-a-z0-9]*$", RegexOptions.Compiled);

    public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;

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
            _ => throw new IntentRenderingException(CommunicationStyle, intent, $"KubernetesAgentIntentRenderer has no native renderer for intent '{intent.Name}' ({intent.GetType().Name}).")
        };
    }

    private static ScriptExecutionRequest RenderRunScript(RunScriptIntent intent, IntentRenderContext context)
    {
        var namespace_ = ResolveNamespace(context);
        var wrappedBody = WrapBodyWithNamespace(intent.ScriptBody, intent.Syntax, namespace_);

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
            Files = new Dictionary<string, byte[]>(),
            PackageReferences = context.PackageReferences.ToList()
        };
    }

    private static ScriptExecutionRequest RenderKubernetesApply(KubernetesApplyIntent intent, IntentRenderContext context)
    {
        var files = ToLegacyFiles(intent.YamlFiles);
        var applyScript = BuildApplyScript(intent);
        var waitScript = KubernetesResourceWaitBuilder.BuildWaitScript(
            files, intent.ObjectStatusCheck, intent.StatusCheckTimeoutSeconds, intent.Namespace, intent.Syntax);
        var rawScript = applyScript + waitScript;
        var wrappedScript = WrapBodyWithNamespace(rawScript, intent.Syntax, ResolveNamespaceForApply(intent.Namespace));

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

    private static Dictionary<string, byte[]> ToLegacyFiles(IReadOnlyList<DeploymentFile> yamlFiles)
    {
        var result = new Dictionary<string, byte[]>(yamlFiles.Count);

        foreach (var file in yamlFiles)
            result[file.RelativePath] = file.Content;

        return result;
    }

    private static ScriptExecutionRequest RenderHelmUpgrade(HelmUpgradeIntent intent, IntentRenderContext context)
    {
        var files = HelmUpgradeScriptBuilder.BuildFiles(intent);
        var rawScript = HelmUpgradeScriptBuilder.Build(intent, intent.Syntax);
        var namespace_ = ResolveNamespaceForApply(intent.Namespace);
        var wrappedScript = WrapBodyWithNamespace(rawScript, intent.Syntax, namespace_);

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
            Files = files,
            PackageReferences = context.PackageReferences.ToList()
        };
    }

    private static ScriptExecutionRequest RenderKustomize(KubernetesKustomizeIntent intent, IntentRenderContext context)
    {
        var rawScript = KubernetesKustomizeScriptBuilder.Build(intent, intent.Syntax);
        var namespace_ = ResolveNamespaceForApply(intent.Namespace);
        var wrappedScript = WrapBodyWithNamespace(rawScript, intent.Syntax, namespace_);

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
            Files = new Dictionary<string, byte[]>(),
            PackageReferences = context.PackageReferences.ToList()
        };
    }

    private static string ResolveNamespace(IntentRenderContext context)
    {
        var ns = context.EffectiveVariables
            .FirstOrDefault(v => v.Name == SpecialVariables.Kubernetes.Namespace)?.Value;

        return string.IsNullOrWhiteSpace(ns) ? KubernetesDefaultValues.Namespace : ns;
    }

    private static string ResolveNamespaceForApply(string intentNamespace)
    {
        return string.IsNullOrWhiteSpace(intentNamespace) ? KubernetesDefaultValues.Namespace : intentNamespace;
    }

    private static string WrapBodyWithNamespace(string scriptBody, ScriptSyntax syntax, string namespace_)
    {
        if (!ScriptSyntaxHelper.IsShellSyntax(syntax))
            return scriptBody;

        ValidateKubernetesName(namespace_);

        return syntax == ScriptSyntax.Bash
            ? WrapBash(scriptBody, namespace_)
            : WrapPowerShell(scriptBody, namespace_);
    }

    private static void ValidateKubernetesName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        if (!ValidKubernetesNameRegex.IsMatch(name))
            throw new ArgumentException($"Invalid Kubernetes namespace name: '{name}'. Must match [a-z0-9][-a-z0-9]*.");
    }

    private static string WrapBash(string script, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""kubectl config set-context --current --namespace="{ns}" > /dev/null 2>&1 || true""");

        if (!string.IsNullOrEmpty(ns) && ns != KubernetesDefaultValues.Namespace)
        {
            sb.AppendLine($"""kubectl get namespace -o name 2>/dev/null | grep -qx "namespace/{ns}" || kubectl create namespace "{ns}" || echo "Warning: Failed to create namespace {ns}, it may already exist" """);
        }

        if (!string.IsNullOrWhiteSpace(script))
            sb.Append(script);

        return sb.ToString();
    }

    private static string WrapPowerShell(string script, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"kubectl config set-context --current --namespace=\"{ns}\" | Out-Null");

        if (!string.IsNullOrEmpty(ns) && ns != KubernetesDefaultValues.Namespace)
        {
            sb.AppendLine($"$existingNs = kubectl get namespace \"{ns}\" --ignore-not-found 2>&1");
            sb.AppendLine("if (-not $existingNs) {");
            sb.AppendLine($"    kubectl create namespace \"{ns}\"");
            sb.AppendLine($"    if ($LASTEXITCODE -ne 0) {{ Write-Warning \"Failed to create namespace {ns}, it may already exist\" }}");
            sb.AppendLine("}");
        }

        if (!string.IsNullOrWhiteSpace(script))
            sb.Append(script);

        return sb.ToString();
    }

}
