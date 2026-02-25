namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface ICalamariPayloadBuilder : IScopedDependency
{
    CalamariPayload Build(ScriptExecutionRequest request);
}
