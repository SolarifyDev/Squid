namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public interface ICalamariPayloadBuilder : IScopedDependency
{
    CalamariPayload Build(ScriptExecutionRequest request);
}
