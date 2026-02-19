using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.Deployments;

public interface IExecutionStrategy : IScopedDependency
{
    bool CanHandle(string communicationStyle);

    Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct);
}
