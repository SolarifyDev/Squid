using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public interface IScriptContextWrapper : IScopedDependency
{
    string WrapScript(string script, string endpointJson, DeploymentAccount account,
                      ScriptSyntax syntax, List<VariableDto> variables);
}
