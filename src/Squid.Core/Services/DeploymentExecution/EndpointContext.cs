using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public class EndpointContext
{
    public string EndpointJson { get; set; }
    public AccountType? AccountType { get; set; }
    public string CredentialsJson { get; set; }
}

public class ScriptContext
{
    public EndpointContext Endpoint { get; set; }
    public ScriptSyntax Syntax { get; set; }
    public List<VariableDto> Variables { get; set; }
}
