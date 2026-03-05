using System.Text.RegularExpressions;
using Squid.Message.Models.Deployments.Execution;

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
        return $"""
            kubectl config set-context --current --namespace="{ns}" > /dev/null 2>&1
            {script}
            """;
    }

    private static string WrapPowerShell(string script, string ns)
    {
        return $"""
            kubectl config set-context --current --namespace="{ns}" | Out-Null
            {script}
            """;
    }
}
