using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiScriptContextWrapper : IScriptContextWrapper
{
    private readonly IKubernetesApiContextScriptBuilder _builder;

    public KubernetesApiScriptContextWrapper(IKubernetesApiContextScriptBuilder builder)
    {
        _builder = builder;
    }

    public string WrapScript(string script, string endpointJson, AccountType? accountType, string credentialsJson,
                             ScriptSyntax syntax, List<VariableDto> variables)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(endpointJson);
        if (endpoint == null) return script;

        var customKubectl = variables?
            .FirstOrDefault(v => string.Equals(v.Name, "Squid.Action.Kubernetes.CustomKubectlExecutable", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return _builder.WrapWithContext(script, endpoint, accountType, credentialsJson, syntax, customKubectl);
    }
}
