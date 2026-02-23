namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public interface ILocalProcessRunner : IScopedDependency
{
    Task<ScriptExecutionResult> RunAsync(string executable, string arguments, string workDir, CancellationToken ct);
}
