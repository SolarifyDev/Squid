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
/// Phase 9j.3 — the KubernetesAgent renderer no longer behaves as a pure pass-through.
///
/// <para>
/// When it sees a <see cref="RunScriptIntent"/>, it constructs a fresh
/// <see cref="ScriptExecutionRequest"/> from the intent plus <see cref="IntentRenderContext"/>,
/// prepending the kubectl namespace preamble (plus a <c>kubectl create namespace</c> probe
/// when the target namespace is non-default) for shell syntaxes (bash / PowerShell). Non-shell
/// syntaxes (Python, ...) pass the script body through unchanged.
/// </para>
///
/// <para>
/// When it sees a <see cref="KubernetesApplyIntent"/>, the renderer synthesises the
/// <c>kubectl apply -f</c> pipeline (one invocation per file, server-side-apply flags from
/// the intent), appends a <see cref="KubernetesResourceWaitBuilder"/> status-check block when
/// <see cref="KubernetesApplyIntent.ObjectStatusCheck"/> is set, and wraps the resulting
/// script with the namespace preamble from <see cref="KubernetesApplyIntent.Namespace"/> for
/// shell syntaxes. <c>Files</c> on the returned request are derived directly from
/// <see cref="KubernetesApplyIntent.YamlFiles"/> — the legacy request is no longer consulted
/// for YAML content.
/// </para>
///
/// <para>
/// Namespace resolution for <see cref="RunScriptIntent"/> — which has no namespace field —
/// reads <c>SpecialVariables.Kubernetes.Namespace</c> from
/// <see cref="IntentRenderContext.EffectiveVariables"/>, falling back to
/// <see cref="KubernetesDefaultValues.Namespace"/>.
/// </para>
///
/// <para>
/// For intents the renderer doesn't know how to render natively yet, it falls back to the
/// Phase-5 pass-through path (return <c>LegacyRequest</c> unchanged, throw
/// <see cref="IntentRenderingException"/> when it is absent).
/// </para>
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
            _ => Task.FromResult(FallbackToLegacy(intent, context))
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

    private ScriptExecutionRequest FallbackToLegacy(ExecutionIntent intent, IntentRenderContext context)
    {
        if (context.LegacyRequest is null)
            throw new IntentRenderingException(
                CommunicationStyle,
                intent,
                "KubernetesAgentIntentRenderer has no native renderer for this intent and IntentRenderContext.LegacyRequest is not populated.");

        return context.LegacyRequest;
    }
}
