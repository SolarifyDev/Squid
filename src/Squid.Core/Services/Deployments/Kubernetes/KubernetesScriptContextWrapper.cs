using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesScriptContextWrapper : IScriptContextWrapper
{
    private readonly IKubernetesContextScriptBuilder _builder;

    public KubernetesScriptContextWrapper(IKubernetesContextScriptBuilder builder)
    {
        _builder = builder;
    }

    public bool CanWrap(string communicationStyle)
        => string.Equals(communicationStyle, "Kubernetes", StringComparison.OrdinalIgnoreCase);

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

    private static KubernetesEndpointDto Deserialize(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<KubernetesEndpointDto>(endpointJson);
        }
        catch
        {
            return null;
        }
    }
}
