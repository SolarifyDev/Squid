using Squid.Core.Services.Deployments;

namespace Squid.E2ETests.Infrastructure;

public class CapturingExecutionStrategy : IExecutionStrategy
{
    public List<ScriptExecutionRequest> CapturedRequests { get; } = new();

    public bool CanHandle(string communicationStyle) => true;

    public Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request, CancellationToken ct)
    {
        CapturedRequests.Add(request);

        return Task.FromResult(new ScriptExecutionResult
        {
            Success = true,
            ExitCode = 0
        });
    }

    public void Clear() => CapturedRequests.Clear();
}
