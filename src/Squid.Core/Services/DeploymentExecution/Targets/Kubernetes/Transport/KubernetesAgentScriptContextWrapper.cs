using System.Text.RegularExpressions;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentScriptContextWrapper : IScriptContextWrapper
{
    private static readonly Regex ValidKubernetesNameRegex = new("^[a-z0-9][-a-z0-9]*$", RegexOptions.Compiled);

    public string WrapScript(string script, ScriptContext context)
    {
        var ns = ResolveNamespace(context);
        ValidateKubernetesName(ns);

        return context?.Syntax == ScriptSyntax.Bash
            ? WrapBash(script, ns)
            : WrapPowerShell(script, ns);
    }

    public static void ValidateKubernetesName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        if (!ValidKubernetesNameRegex.IsMatch(name))
            throw new ArgumentException($"Invalid Kubernetes namespace name: '{name}'. Must match [a-z0-9][-a-z0-9]*.");
    }

    private static string ResolveNamespace(ScriptContext context)
    {
        if (context?.ActionProperties != null && context.ActionProperties.Count > 0)
            return KubernetesPropertyParser.GetNamespace(context.ActionProperties);

        return KubernetesDefaultValues.Namespace;
    }

    private static string WrapBash(string script, string ns)
    {
        var sb = new System.Text.StringBuilder();
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
        var sb = new System.Text.StringBuilder();
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
