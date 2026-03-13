using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface ICalamariPayloadBuilder : IScopedDependency
{
    CalamariPayload Build(ScriptExecutionRequest request);
    CalamariPayload Build(ScriptExecutionRequest request, ScriptSyntax syntax);
}
