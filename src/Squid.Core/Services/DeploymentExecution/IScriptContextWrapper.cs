using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public interface IScriptContextWrapper : IScopedDependency
{
    string WrapScript(string script, string endpointJson, AccountType? accountType, string credentialsJson,
                      ScriptSyntax syntax, List<VariableDto> variables);
}
