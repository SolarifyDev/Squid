using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface ICalamariPayloadBuilder : IScopedDependency
{
    CalamariPayload Build(ScriptExecutionRequest request);
    CalamariPayload Build(ScriptExecutionRequest request, ScriptSyntax syntax);
}
