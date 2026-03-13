using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public abstract class DeploymentTransport : IDeploymentTransport
{
    public CommunicationStyle CommunicationStyle { get; }
    public IEndpointVariableContributor Variables { get; }
    public IScriptContextWrapper ScriptWrapper { get; }
    public IExecutionStrategy Strategy { get; }
    public IHealthCheckStrategy HealthChecker { get; }
    public ExecutionLocation ExecutionLocation { get; }
    public ExecutionBackend ExecutionBackend { get; }
    public bool RequiresContextPreparationForPackagedPayload { get; }

    protected DeploymentTransport(
        CommunicationStyle communicationStyle,
        IEndpointVariableContributor variables,
        IScriptContextWrapper scriptWrapper,
        IExecutionStrategy strategy,
        IHealthCheckStrategy healthChecker = null,
        ExecutionLocation executionLocation = ExecutionLocation.Unspecified,
        ExecutionBackend executionBackend = ExecutionBackend.Unspecified,
        bool requiresContextPreparationForPackagedPayload = false)
    {
        CommunicationStyle = communicationStyle;
        Variables = variables;
        ScriptWrapper = scriptWrapper;
        Strategy = strategy;
        HealthChecker = healthChecker;
        ExecutionLocation = executionLocation;
        ExecutionBackend = executionBackend;
        RequiresContextPreparationForPackagedPayload = requiresContextPreparationForPackagedPayload;
    }
}
