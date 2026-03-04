using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentScriptContextWrapper : IScriptContextWrapper
{
    public string WrapScript(string script, ScriptContext context)
    {
        var ns = ResolveNamespace(context);

        return context?.Syntax == ScriptSyntax.Bash
            ? WrapBash(script, ns)
            : WrapPowerShell(script, ns);
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
