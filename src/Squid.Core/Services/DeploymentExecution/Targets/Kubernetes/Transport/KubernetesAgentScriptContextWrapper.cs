using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentScriptContextWrapper : IScriptContextWrapper
{
    public string WrapScript(string script, string endpointJson, AccountType? accountType, string credentialsJson,
                             ScriptSyntax syntax, List<VariableDto> variables)
    {
        var ns = ResolveNamespace(variables);

        return syntax == ScriptSyntax.Bash
            ? WrapBash(script, ns)
            : WrapPowerShell(script, ns);
    }

    private static string ResolveNamespace(List<VariableDto> variables)
    {
        var ns = variables?
            .FirstOrDefault(v => string.Equals(v.Name, "Squid.Action.Kubernetes.Namespace", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(ns) ? "default" : ns;
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
