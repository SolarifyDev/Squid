using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface ILocalProcessRunner : IScopedDependency
{
    Task<ScriptExecutionResult> RunAsync(string executable, string arguments, string workDir, CancellationToken ct, TimeSpan? timeout = null, SensitiveValueMasker masker = null, Dictionary<string, string> environmentVariables = null);
}
