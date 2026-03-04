namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface ILocalProcessRunner : IScopedDependency
{
    Task<ScriptExecutionResult> RunAsync(string executable, string arguments, string workDir, CancellationToken ct);
}
