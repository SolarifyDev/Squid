using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentScriptContextWrapper : IScriptContextWrapper
{
    public string WrapScript(string script, ScriptContext context)
    {
        var ns = ResolveNamespace(context?.Variables);

        return context?.Syntax == ScriptSyntax.Bash
            ? WrapBash(script, ns)
            : WrapPowerShell(script, ns);
    }

    private static string ResolveNamespace(List<VariableDto> variables)
    {
        var ns = variables?
            .FirstOrDefault(v => string.Equals(v.Name, KubernetesProperties.LegacyNamespace, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(ns) ? KubernetesDefaultValues.Namespace : ns;
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
