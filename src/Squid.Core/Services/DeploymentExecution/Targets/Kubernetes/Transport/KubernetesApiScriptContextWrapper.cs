using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiScriptContextWrapper : IScriptContextWrapper
{
    private readonly IKubernetesApiContextScriptBuilder _builder;

    public KubernetesApiScriptContextWrapper(IKubernetesApiContextScriptBuilder builder)
    {
        _builder = builder;
    }

    public string WrapScript(string script, ScriptContext context)
    {
        if (!ScriptSyntaxHelper.IsShellSyntax(context?.Syntax ?? ScriptSyntax.Bash))
            return script;

        var endpoint = EndpointVariableFactory.TryDeserialize<Message.Models.Deployments.Machine.KubernetesApiEndpointDto>(context?.Endpoint?.EndpointJson);

        if (endpoint == null) return script;

        var customKubectl = context.Variables?
            .FirstOrDefault(v => string.Equals(v.Name, SpecialVariables.Kubernetes.CustomKubectlExecutable, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return _builder.WrapWithContext(script, context, customKubectl);
    }
}
