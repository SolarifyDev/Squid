namespace Squid.Core.Services.DeploymentExecution;

public interface IExecutionStrategy : IScopedDependency
{
    Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct);
}
