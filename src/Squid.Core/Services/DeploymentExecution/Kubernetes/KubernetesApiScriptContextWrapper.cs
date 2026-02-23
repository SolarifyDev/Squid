using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
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

    public string WrapScript(string script, string endpointJson, DeploymentAccount account,
                             ScriptSyntax syntax, List<VariableDto> variables)
    {
        var endpoint = Deserialize(endpointJson);
        if (endpoint == null) return script;

        var customKubectl = variables?
            .FirstOrDefault(v => string.Equals(v.Name, "Squid.Action.Kubernetes.CustomKubectlExecutable", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return _builder.WrapWithContext(script, endpoint, account, syntax, customKubectl);
    }

    private static KubernetesApiEndpointDto Deserialize(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<KubernetesApiEndpointDto>(endpointJson);
        }
        catch
        {
            return null;
        }
    }
}
