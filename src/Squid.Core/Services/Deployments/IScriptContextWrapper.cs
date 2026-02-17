using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public interface IScriptContextWrapper : IScopedDependency
{
    bool CanWrap(string communicationStyle);

    string WrapScript(string script, string endpointJson, DeploymentAccount account,
                      ScriptSyntax syntax, List<VariableDto> variables);
}
