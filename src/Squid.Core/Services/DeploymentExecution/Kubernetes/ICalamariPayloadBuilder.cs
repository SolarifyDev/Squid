namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public interface ICalamariPayloadBuilder : IScopedDependency
{
    string ResolvedVersion { get; }

    CalamariPayload Build(ScriptExecutionRequest request);
}
